using System.Data;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using AiDatabaseAgent.Core.Observability;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.Infrastructure.Observability;

public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>
    /// Connection to the agent's own operational database (AgentOps, see database/ops/agent-ops.sql), never the
    /// business database the agent queries. Empty disables the audit log. The identity needs only INSERT on
    /// audit.QueryLog.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Records waiting to be written; when full, new records are dropped (and counted) rather than blocking answers.</summary>
    public int QueueCapacity { get; set; } = 1000;

    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);
}

/// <summary>An in-memory queue in front of the database: requests enqueue, <see cref="QueryAuditWriter"/> drains it.</summary>
public sealed class QueryAuditBuffer : IQueryAuditLog
{
    private static readonly Meter Meter = new(AgentTelemetry.SourceName);
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>(
        "agent.audit.dropped", unit: "{record}", description: "Audit records dropped because the queue was full or the write failed.");

    private readonly Channel<QueryAuditRecord> _channel;
    private readonly bool _enabled;

    public QueryAuditBuffer(IOptions<AuditOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var o = options.Value;
        _enabled = o.Enabled;
        _channel = Channel.CreateBounded<QueryAuditRecord>(new BoundedChannelOptions(Math.Max(1, o.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
    }

    internal ChannelReader<QueryAuditRecord> Reader => _channel.Reader;

    public bool TryWrite(QueryAuditRecord record)
    {
        if (!_enabled)
            return false;
        if (_channel.Writer.TryWrite(record))
            return true;
        Dropped.Add(1, new KeyValuePair<string, object?>("reason", "queue_full"));
        return false;
    }

    internal static void CountFailed(int records) => Dropped.Add(records, new KeyValuePair<string, object?>("reason", "write_failed"));

    internal void Complete() => _channel.Writer.TryComplete();
}

/// <summary>Writes queued audit records to audit.QueryLog. A failing audit database costs records, never answers.</summary>
public sealed partial class QueryAuditWriter(QueryAuditBuffer queue, IOptions<AuditOptions> options, ILogger<QueryAuditWriter> logger) : BackgroundService
{
    internal const string InsertSql = """
        INSERT INTO audit.QueryLog
            (AskedAtUtc, TraceId, Version, Model, Question, Outcome, [Sql], Message, Attempts, ToolCalls, RowsReturned,
             Truncated, EstimatedCost, TotalMs, ModelMs, QueryMs, InputTokens, OutputTokens, KnowledgeIds)
        VALUES
            (@AskedAtUtc, @TraceId, @Version, @Model, @Question, @Outcome, @Sql, @Message, @Attempts, @ToolCalls, @RowsReturned,
             @Truncated, @EstimatedCost, @TotalMs, @ModelMs, @QueryMs, @InputTokens, @OutputTokens, @KnowledgeIds);
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        var reader = queue.Reader;
        try
        {
            // Only the wait is cancellable: a batch already taken off the queue is always written, so a
            // shutdown in the middle of a write doesn't lose it.
            while (await reader.WaitToReadAsync(stoppingToken))
                await WriteAsync(Drain(reader, 100), CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: flush what is already queued, without waiting for more.
            var rest = Drain(reader, int.MaxValue);
            if (rest.Count > 0)
                await WriteAsync(rest, CancellationToken.None);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        return base.StopAsync(cancellationToken);
    }

    private static List<QueryAuditRecord> Drain(ChannelReader<QueryAuditRecord> reader, int max)
    {
        var batch = new List<QueryAuditRecord>();
        while (batch.Count < max && reader.TryRead(out var record))
            batch.Add(record);
        return batch;
    }

    private async Task WriteAsync(List<QueryAuditRecord> batch, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(options.Value.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            foreach (var record in batch)
            {
                await using var cmd = new SqlCommand(InsertSql, connection);
                AddParameters(cmd, record);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            QueryAuditBuffer.CountFailed(batch.Count);
            LogWriteFailed(logger, batch.Count, ex);
        }
    }

    internal static void AddParameters(SqlCommand cmd, QueryAuditRecord r)
    {
        var p = cmd.Parameters;
        p.Add("@AskedAtUtc", SqlDbType.DateTime2).Value = r.AskedAt.UtcDateTime;
        p.Add("@TraceId", SqlDbType.Char, 32).Value = (object?)r.TraceId ?? DBNull.Value;
        p.Add("@Version", SqlDbType.VarChar, 4).Value = r.Version;
        p.Add("@Model", SqlDbType.NVarChar, 100).Value = Truncate(r.Model, 100);
        p.Add("@Question", SqlDbType.NVarChar, 1000).Value = Truncate(r.Question, 1000);
        p.Add("@Outcome", SqlDbType.VarChar, 20).Value = r.Outcome;
        p.Add("@Sql", SqlDbType.NVarChar, -1).Value = (object?)r.Sql ?? DBNull.Value;
        p.Add("@Message", SqlDbType.NVarChar, 1000).Value = r.Message is null ? DBNull.Value : Truncate(r.Message, 1000);
        p.Add("@Attempts", SqlDbType.SmallInt).Value = r.Attempts;
        p.Add("@ToolCalls", SqlDbType.SmallInt).Value = r.ToolCalls;
        p.Add("@RowsReturned", SqlDbType.Int).Value = (object?)r.RowsReturned ?? DBNull.Value;
        p.Add("@Truncated", SqlDbType.Bit).Value = (object?)r.Truncated ?? DBNull.Value;
        p.Add("@EstimatedCost", SqlDbType.Float).Value = (object?)r.EstimatedCost ?? DBNull.Value;
        p.Add("@TotalMs", SqlDbType.Int).Value = r.TotalMs;
        p.Add("@ModelMs", SqlDbType.Int).Value = r.ModelMs;
        p.Add("@QueryMs", SqlDbType.Int).Value = (object?)r.QueryMs ?? DBNull.Value;
        p.Add("@InputTokens", SqlDbType.Int).Value = r.InputTokens is { } input ? (int)input : DBNull.Value;
        p.Add("@OutputTokens", SqlDbType.Int).Value = r.OutputTokens is { } output ? (int)output : DBNull.Value;
        p.Add("@KnowledgeIds", SqlDbType.NVarChar, 400).Value = r.KnowledgeIds is null ? DBNull.Value : Truncate(r.KnowledgeIds, 400);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit log disabled (Audit:ConnectionString is empty)")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write {Count} audit record(s); they were dropped")]
    private static partial void LogWriteFailed(ILogger logger, int count, Exception ex);
}

/// <summary>Audit database reachable and writable. Degraded, never unhealthy: answers don't depend on it.</summary>
public sealed class AuditHealthCheck(IOptions<AuditOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        if (!o.Enabled)
            return HealthCheckResult.Healthy("Audit log disabled (no Audit:ConnectionString).");
        try
        {
            await using var connection = new SqlConnection(o.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand("SELECT HAS_PERMS_BY_NAME(N'audit.QueryLog', N'OBJECT', N'INSERT');", connection);
            return await cmd.ExecuteScalarAsync(cancellationToken) is 1
                ? HealthCheckResult.Healthy("Questions are recorded in audit.QueryLog.")
                : HealthCheckResult.Degraded("Connected, but audit.QueryLog is missing or not writable; questions are not being recorded.");
        }
        catch (SqlException ex)
        {
            return HealthCheckResult.Degraded($"Audit database not reachable ({ex.Message}); questions are not being recorded.");
        }
    }
}

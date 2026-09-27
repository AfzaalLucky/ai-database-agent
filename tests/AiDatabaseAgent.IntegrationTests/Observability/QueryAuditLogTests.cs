using AiDatabaseAgent.Core.Observability;
using AiDatabaseAgent.Infrastructure.Observability;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.IntegrationTests.Observability;

/// <summary>
/// The audit writer against a real AgentOps database (database/ops/agent-ops.sql). Override the connection with
/// the <c>AGENTOPS_CONNECTION</c> environment variable.
/// </summary>
public class QueryAuditLogTests
{
    private static readonly string Connection = Environment.GetEnvironmentVariable("AGENTOPS_CONNECTION")
        ?? "Server=localhost;Database=AgentOps;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=AiDatabaseAgent.Tests";

    private static IOptions<AuditOptions> Options(string connection) =>
        Microsoft.Extensions.Options.Options.Create(new AuditOptions { ConnectionString = connection });

    [Fact]
    public async Task Queued_records_are_written_and_flushed_on_shutdown()
    {
        var traceId = Guid.NewGuid().ToString("N");
        var buffer = new QueryAuditBuffer(Options(Connection));
        using var writer = new QueryAuditWriter(buffer, Options(Connection), NullLogger<QueryAuditWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);

        Assert.True(buffer.TryWrite(Record(traceId, "Answered", "SELECT Status FROM sales.Orders")));
        Assert.True(buffer.TryWrite(Record(traceId, "NotAnswerable", null)));
        await writer.StopAsync(CancellationToken.None);

        await using var connection = new SqlConnection(Connection);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand("SELECT Outcome, [Sql], InputTokens, KnowledgeIds FROM audit.QueryLog WHERE TraceId = @t ORDER BY QueryLogId", connection);
        cmd.Parameters.AddWithValue("@t", traceId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string Outcome, string? Sql, int? Tokens, string? Knowledge)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3)));

        await reader.CloseAsync();

        // Leave the real audit log as it was.
        await using var cleanup = new SqlCommand("DELETE FROM audit.QueryLog WHERE TraceId = @t", connection);
        cleanup.Parameters.AddWithValue("@t", traceId);
        await cleanup.ExecuteNonQueryAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(("Answered", "SELECT Status FROM sales.Orders", 2503, "order-status"), rows[0]);
        Assert.Equal("NotAnswerable", rows[1].Outcome);
        Assert.Null(rows[1].Sql);
    }

    [Fact]
    public void A_disabled_log_accepts_nothing()
    {
        var buffer = new QueryAuditBuffer(Options(""));

        Assert.False(buffer.TryWrite(Record("x", "Answered", null)));
    }

    [Fact]
    public async Task An_unreachable_audit_database_is_degraded_not_unhealthy()
    {
        var check = new AuditHealthCheck(Options("Server=localhost;Database=AgentOpsDoesNotExist;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5"));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task The_real_audit_database_is_healthy()
    {
        var result = await new AuditHealthCheck(Options(Connection)).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private static QueryAuditRecord Record(string traceId, string outcome, string? sql) => new(
        DateTimeOffset.UtcNow, traceId, "V4", "qwen2.5-coder:7b", "Integration test question", outcome, sql, null,
        1, 0, sql is null ? null : 2, sql is null ? null : false, 0.02, 1500, 1400, sql is null ? null : 3,
        sql is null ? null : 2503, sql is null ? null : 28, sql is null ? null : "order-status");
}

using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using AiDatabaseAgent.Core.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.Infrastructure.SqlServer;

/// <summary>
/// Runs agent SQL on SQL Server as the least-privilege agent user, with a command timeout and a row
/// cap. This is the safety floor for every agent version: whatever the validator lets through, the
/// database itself still refuses writes, DDL and anything outside the agent's grants.
/// </summary>
public sealed class SqlServerQueryExecutor(IOptions<SqlServerOptions> options) : IQueryExecutor
{
    private readonly SqlServerOptions _options = options.Value;

    public async Task<QueryResult> ExecuteAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var session = await ImpersonatedSession.OpenAsync(_options.ConnectionString, _options.AgentUser, cancellationToken);
            await using var cmd = session.CreateCommand(sql, limits.TimeoutSeconds);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => new ResultColumn(string.IsNullOrEmpty(reader.GetName(i)) ? $"(column {i + 1})" : reader.GetName(i), reader.GetDataTypeName(i)))
                .ToList();

            var rows = new List<object?[]>();
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count == limits.MaxRows)
                {
                    truncated = true;
                    cmd.Cancel(); // stop the server producing rows nobody will read
                    break;
                }
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = ToClr(reader.GetValue(i));
                rows.Add(row);
            }
            return new QueryResult(columns, rows, truncated, stopwatch.Elapsed);
        }
        catch (SqlException ex)
        {
            throw new QueryExecutionException(Describe(ex), ex.Number, ex);
        }
    }

    public async Task<QueryCostEstimate> EstimateAsync(string sql, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await ImpersonatedSession.OpenAsync(_options.ConnectionString, _options.AgentUser, cancellationToken);
            await using (var on = session.CreateCommand("SET SHOWPLAN_XML ON;", 15))
                await on.ExecuteNonQueryAsync(cancellationToken);
            try
            {
                double cost = 0, rows = 0;
                await using var cmd = session.CreateCommand(sql, 15);
                await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
                {
                    do
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var plan = XDocument.Parse(reader.GetString(0));
                            foreach (var stmt in plan.Descendants().Where(e => e.Name.LocalName == "StmtSimple"))
                            {
                                cost += ParseDouble(stmt.Attribute("StatementSubTreeCost")?.Value);
                                rows += ParseDouble(stmt.Attribute("StatementEstRows")?.Value);
                            }
                        }
                    } while (await reader.NextResultAsync(cancellationToken));
                }
                return new QueryCostEstimate(cost, rows);
            }
            finally
            {
                // Must be switched off before the session reverts: under SHOWPLAN nothing executes, REVERT included.
                await using var off = session.CreateCommand("SET SHOWPLAN_XML OFF;", 15);
                await off.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        catch (SqlException ex)
        {
            throw new QueryExecutionException(Describe(ex), ex.Number, ex);
        }
    }

    private static double ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static string Describe(SqlException ex) => ex.Number switch
    {
        -2 => "The query timed out and was cancelled.",
        _ => string.Join(" ", ex.Errors.Cast<SqlError>().Select(e => e.Message).Distinct()),
    };

    /// <summary>Maps provider values to plain CLR values that serialize predictably.</summary>
    private static object? ToClr(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => "0x" + Convert.ToHexString(bytes.Length > 64 ? bytes[..64] : bytes) + (bytes.Length > 64 ? "…" : ""),
        Guid g => g.ToString(),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        _ => value,
    };
}

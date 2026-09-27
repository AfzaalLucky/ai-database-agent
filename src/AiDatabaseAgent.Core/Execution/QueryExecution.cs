namespace AiDatabaseAgent.Core.Execution;

/// <summary>Hard limits applied to every query the agent runs, whatever produced the SQL.</summary>
/// <param name="MaxRows">Rows returned to the caller; reading stops after this many (the result is flagged as truncated).</param>
/// <param name="TimeoutSeconds">Command timeout; the server-side query is cancelled when it expires.</param>
public sealed record QueryLimits(int MaxRows = 500, int TimeoutSeconds = 15);

public sealed record ResultColumn(string Name, string SqlType);

/// <summary>A tabular result. Values are CLR primitives (string, long, decimal, DateTime, bool, ...) or null.</summary>
public sealed record QueryResult(
    IReadOnlyList<ResultColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    TimeSpan Elapsed);

/// <summary>An estimated plan summary (from SHOWPLAN_XML), used to refuse unexpectedly expensive queries.</summary>
public sealed record QueryCostEstimate(double EstimatedCost, double EstimatedRows);

/// <summary>A failure reported by the database (syntax, permission, timeout, ...).</summary>
public sealed class QueryExecutionException(string message, int? errorNumber = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>SQL Server error number when available (e.g. 207 invalid column, 229 permission denied, -2 timeout).</summary>
    public int? ErrorNumber { get; } = errorNumber;
}

public interface IQueryExecutor
{
    /// <summary>Runs one read-only query under the agent's restricted identity.</summary>
    /// <exception cref="QueryExecutionException">The database rejected or failed the query.</exception>
    Task<QueryResult> ExecuteAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default);

    /// <summary>Compiles the query (without running it) and returns the optimizer's estimate.</summary>
    /// <exception cref="QueryExecutionException">The query does not compile or is not permitted.</exception>
    Task<QueryCostEstimate> EstimateAsync(string sql, CancellationToken cancellationToken = default);
}

using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Execution;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace AiDatabaseAgent.Infrastructure;

/// <summary>Checks the whole database path: connect, impersonate the agent user, run a query, revert.</summary>
public sealed class DatabaseHealthCheck(IQueryExecutor executor) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await executor.ExecuteAsync("SELECT USER_NAME()", new QueryLimits(1, 5), cancellationToken);
            return HealthCheckResult.Healthy($"Queries run as {r.Rows[0][0]}.");
        }
        catch (QueryExecutionException ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            return HealthCheckResult.Unhealthy("Cannot reach the database.", ex);
        }
    }
}

/// <summary>Checks that Ollama is reachable and has the configured models pulled (the V5 tool model too).</summary>
public sealed class OllamaHealthCheck(IOptions<LlmOptions> options, IOptions<AgentOptions> agent) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var llm = options.Value;
        try
        {
            using var http = new HttpClient { BaseAddress = llm.Endpoint, Timeout = TimeSpan.FromSeconds(5) };
            using var client = new OllamaApiClient(http);
            var pulled = (await client.ListLocalModelsAsync(cancellationToken)).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!pulled.Contains(llm.Model))
                return HealthCheckResult.Unhealthy($"Model {llm.Model} is not pulled. Run: ollama pull {llm.Model}");
            var tool = agent.Value.ToolModel;
            if (tool is not null && !pulled.Contains(tool))
                return HealthCheckResult.Degraded($"{llm.Model} is available; the V5 tool model {tool} is not pulled (V5 unavailable). Run: ollama pull {tool}");
            return HealthCheckResult.Healthy(tool is null || tool == llm.Model ? $"{llm.Model} is available." : $"{llm.Model} and {tool} (V5) are available.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy($"Ollama is not reachable at {llm.Endpoint}.", ex);
        }
    }
}

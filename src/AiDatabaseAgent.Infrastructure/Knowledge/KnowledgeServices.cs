using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.Infrastructure.Knowledge;

/// <summary>Indexes the knowledge base in the background at start-up, so the first V4 question doesn't wait.</summary>
public sealed partial class KnowledgeWarmup(QdrantKnowledgeStore store, ILogger<KnowledgeWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await store.EnsureIndexedAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not fatal: V1–V3 don't need it, and V4 retries indexing on its first question.
            LogWarmupFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Knowledge base indexing failed at start-up; V4 will retry on first use")]
    private static partial void LogWarmupFailed(ILogger logger, Exception ex);
}

/// <summary>Qdrant reachable and the knowledge collection populated.</summary>
public sealed class KnowledgeHealthCheck(QdrantKnowledgeStore store, IOptions<KnowledgeOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        try
        {
            await store.Client.HealthAsync(cancellationToken);
            if (!await store.Client.CollectionExistsAsync(o.Collection, cancellationToken))
                return HealthCheckResult.Degraded($"Qdrant is up; collection {o.Collection} is not indexed yet (V4 unavailable until it is).");
            var count = await store.Client.CountAsync(o.Collection, cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy($"{count} knowledge entries in {o.Collection}.");
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or HttpRequestException)
        {
            return HealthCheckResult.Degraded($"Qdrant is not reachable at {o.QdrantHost}:{o.QdrantGrpcPort} (V4 unavailable; V1–V3 work). Start it with: docker compose up -d", ex);
        }
    }
}

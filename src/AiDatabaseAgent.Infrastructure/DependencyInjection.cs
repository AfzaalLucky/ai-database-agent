using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Knowledge;
using AiDatabaseAgent.Core.Observability;
using AiDatabaseAgent.Core.Schema;
using AiDatabaseAgent.Infrastructure.Knowledge;
using AiDatabaseAgent.Infrastructure.Observability;
using AiDatabaseAgent.Infrastructure.SqlServer;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace AiDatabaseAgent.Infrastructure;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Only "Ollama" is wired up in this version; the agent itself depends on IChatClient, not on a provider.</summary>
    public string Provider { get; set; } = "Ollama";

    /// <summary>
    /// 127.0.0.1, not "localhost": on Windows "localhost" resolves to ::1 first, and Ollama listens on IPv4 only,
    /// so every new connection waited ~2 s for the IPv6 attempt to fail (found in a trace; see ADR 0011).
    /// </summary>
    public Uri Endpoint { get; set; } = new("http://127.0.0.1:11434");

    public string Model { get; set; } = "qwen2.5-coder:7b";

    /// <summary>Ollama's default context is 2048 tokens — too small for a schema-aware prompt.</summary>
    public int ContextWindow { get; set; } = 8192;

    /// <summary>Generous: the first request after start-up also loads the model into memory.</summary>
    public int TimeoutSeconds { get; set; } = 180;
}

public static class DependencyInjection
{
    public static IServiceCollection AddAgentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SqlServerOptions>()
            .Bind(configuration.GetSection(SqlServerOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "SqlServer:ConnectionString is required.")
            .ValidateOnStart();
        services.AddOptions<LlmOptions>()
            .Bind(configuration.GetSection(LlmOptions.SectionName))
            .Validate(o => string.Equals(o.Provider, "Ollama", StringComparison.OrdinalIgnoreCase), "Llm:Provider must be 'Ollama' in this version.")
            .ValidateOnStart();
        services.AddOptions<AgentOptions>().Bind(configuration.GetSection(AgentOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SqlServerSchemaProvider>();
        services.AddSingleton<ISchemaProvider>(sp => sp.GetRequiredService<SqlServerSchemaProvider>());
        services.AddSingleton<IQueryExecutor, SqlServerQueryExecutor>();

        services.AddChatClient(sp =>
            {
                var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
                var http = new HttpClient { BaseAddress = llm.Endpoint, Timeout = TimeSpan.FromSeconds(llm.TimeoutSeconds) };
                return new OllamaApiClient(http, llm.Model);
            })
            .Use((inner, sp) =>
            {
                // Applied to every request, so no caller can forget it.
                var contextWindow = sp.GetRequiredService<IOptions<LlmOptions>>().Value.ContextWindow;
                return new ConfigureOptionsChatClient(inner, o => o.AddOllamaOption(OllamaOption.NumCtx, contextWindow));
            })
            // gen_ai spans and token/duration metrics per model call (prompts and replies are not recorded).
            .UseOpenTelemetry(sourceName: AgentTelemetry.SourceName)
            .UseLogging();

        // V4: business knowledge in Qdrant, embedded with an Ollama embedding model (keyed, so it is never
        // confused with the chat client).
        services.AddOptions<KnowledgeOptions>().Bind(configuration.GetSection(KnowledgeOptions.SectionName));
        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>("knowledge", (sp, _) =>
        {
            var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            var knowledge = sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
            var http = new HttpClient { BaseAddress = llm.Endpoint, Timeout = TimeSpan.FromSeconds(llm.TimeoutSeconds) };
            IEmbeddingGenerator<string, Embedding<float>> ollama = new OllamaApiClient(http, knowledge.EmbeddingModel);
            return ollama
                .AsBuilder()
                .UseOpenTelemetry(sourceName: AgentTelemetry.SourceName)
                .Build(sp);
        });
        services.AddSingleton<QdrantKnowledgeStore>();
        services.AddSingleton<IKnowledgeRetriever>(sp => sp.GetRequiredService<QdrantKnowledgeStore>());
        services.AddHostedService<KnowledgeWarmup>();

        services.AddSingleton<TextToSqlAgent>();

        // Audit log: every question, in the agent's own AgentOps database (off the request path).
        services.AddOptions<AuditOptions>().Bind(configuration.GetSection(AuditOptions.SectionName));
        services.AddSingleton<QueryAuditBuffer>();
        services.AddSingleton<IQueryAuditLog>(sp => sp.GetRequiredService<QueryAuditBuffer>());
        services.AddHostedService<QueryAuditWriter>();

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database")
            .AddCheck<OllamaHealthCheck>("llm")
            .AddCheck<KnowledgeHealthCheck>("knowledge")
            .AddCheck<AuditHealthCheck>("audit");
        return services;
    }
}

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AiDatabaseAgent.Api;
using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Observability;
using AiDatabaseAgent.Infrastructure;
using AiDatabaseAgent.Infrastructure.SqlServer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Requests are a question and a version: nothing legitimate comes close to this.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024);

builder.Services.AddAgentInfrastructure(builder.Configuration);
builder.Services.AddOptions<UiOptions>().Bind(builder.Configuration.GetSection(UiOptions.SectionName));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// OpenTelemetry: the agent's own spans and metrics (AgentTelemetry), model calls (Microsoft.Extensions.AI),
// HTTP in and out (Ollama, Qdrant). Exported over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set — e.g. to the
// Aspire dashboard in docker-compose (profile "observability") or any OpenTelemetry collector.
var otlpEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "ai-database-agent"))
    .WithTracing(t => t
        .AddSource(AgentTelemetry.SourceName)
        .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation())
    .WithMetrics(m => m
        .AddMeter(AgentTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
});
if (otlpEnabled)
    otel.UseOtlpExporter();

// Behind a reverse proxy (Nginx on the VPS) the client address arrives in X-Forwarded-For. Only the
// configured proxies are trusted, so a client can't pick its own rate-limit partition.
var knownProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
if (knownProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = 1;
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
        foreach (var proxy in knownProxies)
            o.KnownProxies.Add(IPAddress.Parse(proxy));
    });
}

// /api/ask drives a language model and a database: cap it per client address, and cap how many run at
// once (a local model serves one request at a time; the rest wait in a short queue, then get 429).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var concurrency = builder.Configuration.GetValue("RateLimit:AskConcurrency", 4);
    var queue = builder.Configuration.GetValue("RateLimit:AskQueue", 16);
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        http.Request.Path.StartsWithSegments("/api/ask")
            ? RateLimitPartition.GetConcurrencyLimiter("ask", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = concurrency,
                QueueLimit = queue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            })
            : RateLimitPartition.GetNoLimiter("other"));
    o.AddPolicy("ask", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimit:AskPerMinute", 20),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

if (knownProxies.Length > 0)
    app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h.XFrameOptions = "DENY";
    h.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

var api = app.MapGroup("/api");

api.MapPost("/ask", async Task<Results<Ok<AskResponse>, ProblemHttpResult>> (
        AskRequest request, TextToSqlAgent agent, IQueryAuditLog audit, IOptions<AgentOptions> agentOptions,
        IOptions<LlmOptions> llm, TimeProvider time, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return TypedResults.Problem("Ask a question.", statusCode: StatusCodes.Status400BadRequest);

        var askedAt = time.GetUtcNow();
        var clock = Stopwatch.StartNew();
        var traceId = Activity.Current?.TraceId.ToHexString();

        // Infrastructure failures are recorded too: they are exactly what an operator needs to see.
        ProblemHttpResult Unavailable(string detail)
        {
            var version = request.Version ?? agentOptions.Value.DefaultVersion;
            var model = version == AgentVersion.V5 ? agentOptions.Value.ToolModel ?? llm.Value.Model : llm.Value.Model;
            audit.TryWrite(QueryAuditRecord.ForError(request.Question.Trim(), version, model, detail, clock.Elapsed, askedAt, traceId));
            return TypedResults.Problem(detail, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var result = await agent.AskAsync(request.Question, request.Version, ct);
            audit.TryWrite(QueryAuditRecord.From(result, askedAt, traceId));
            return TypedResults.Ok(AskResponse.From(result, traceId));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (NotSupportedException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (HttpRequestException)
        {
            return Unavailable("The language model is not reachable. Is Ollama running?");
        }
        catch (Grpc.Core.RpcException)
        {
            return Unavailable("The knowledge base (Qdrant) is not reachable, so V4 is unavailable. Start it with `docker compose up -d`, or ask with V1–V3.");
        }
    })
    .RequireRateLimiting("ask")
    .DisableRequestTimeout()
    .WithSummary("Answer a natural-language question with SQL and its result.");

api.MapGet("/prompt", async Task<Results<Ok<PromptResponse>, ProblemHttpResult>> (string question, AgentVersion? version, TextToSqlAgent agent, IOptions<AgentOptions> options, CancellationToken ct) =>
    {
        var v = version ?? options.Value.DefaultVersion;
        try
        {
            var messages = await agent.PreviewPromptAsync(question, v, ct);
            return TypedResults.Ok(new PromptResponse(v, messages.Select(m => new PromptMessage(m.Role.Value, m.Text)).ToList()));
        }
        catch (NotSupportedException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    })
    .WithSummary("The exact messages a version would send to the model for a question.");

api.MapGet("/schema", async (SqlServerSchemaProvider schema, CancellationToken ct) =>
    {
        var s = await schema.GetSchemaAsync(ct);
        return TypedResults.Ok(new SchemaResponse(s.DatabaseName, s.Tables));
    })
    .WithSummary("The schema exactly as the agent (and the model) sees it.");

api.MapGet("/examples", (IOptions<UiOptions> ui) => TypedResults.Ok(ui.Value.SampleQuestions))
    .WithSummary("Sample questions for the UI.");

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), e.Value.Description }),
        });
    },
    ResultStatusCodes = { [HealthStatus.Degraded] = StatusCodes.Status200OK, [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable },
});

app.Run();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;

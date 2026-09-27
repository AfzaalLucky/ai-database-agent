using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AiDatabaseAgent.Api;
using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Infrastructure;
using AiDatabaseAgent.Infrastructure.SqlServer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAgentInfrastructure(builder.Configuration);
builder.Services.AddOptions<UiOptions>().Bind(builder.Configuration.GetSection(UiOptions.SectionName));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// /api/ask drives a language model and a database: cap it per client address.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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

api.MapPost("/ask", async Task<Results<Ok<AskResponse>, ProblemHttpResult>> (AskRequest request, TextToSqlAgent agent, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return TypedResults.Problem("Ask a question.", statusCode: StatusCodes.Status400BadRequest);
        try
        {
            return TypedResults.Ok(AskResponse.From(await agent.AskAsync(request.Question, request.Version, ct)));
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
            return TypedResults.Problem("The language model is not reachable. Is Ollama running?", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Grpc.Core.RpcException)
        {
            return TypedResults.Problem("The knowledge base (Qdrant) is not reachable, so V4 is unavailable. Start it with `docker compose up -d`, or ask with V1–V3.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    })
    .RequireRateLimiting("ask")
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

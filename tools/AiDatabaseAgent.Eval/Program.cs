// Evaluation runner: asks every question in eval/questions.json through each agent version and scores
// the result against the gold query's result (execution accuracy). Writes a Markdown and a JSON report.
//
//   dotnet run --project tools/AiDatabaseAgent.Eval                       # all versions, all questions
//   dotnet run --project tools/AiDatabaseAgent.Eval -- --versions V3 --filter revenue
//   dotnet run --project tools/AiDatabaseAgent.Eval -- --questions eval/questions-holdout.json
//   dotnet run --project tools/AiDatabaseAgent.Eval -- --model qwen2.5:7b          # every version on one model
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDatabaseAgent.Core.Agent;
using AiDatabaseAgent.Core.Evaluation;
using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Validation;
using AiDatabaseAgent.Core.Schema;
using AiDatabaseAgent.Infrastructure;
using AiDatabaseAgent.Infrastructure.Knowledge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var options = EvalOptions.Parse(args);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    EnvironmentName = Environments.Development,
    ContentRootPath = AppContext.BaseDirectory,
});
if (options.Model is not null)
{
    builder.Configuration["Llm:Model"] = options.Model;
    builder.Configuration["Agent:ToolModel"] = options.Model;
}
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("AiDatabaseAgent", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Services.AddAgentInfrastructure(builder.Configuration);
using var host = builder.Build();

var agent = host.Services.GetRequiredService<TextToSqlAgent>();
var executor = host.Services.GetRequiredService<IQueryExecutor>();
var schema = await host.Services.GetRequiredService<ISchemaProvider>().GetSchemaAsync();
var model = host.Services.GetRequiredService<IOptions<LlmOptions>>().Value.Model;
var toolModel = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value.ToolModel;
if (options.Versions.Contains(AgentVersion.V5) && toolModel is not null && toolModel != model)
    model = $"{model}+{toolModel}";

// V4 retrieves from the knowledge base: make sure the index matches knowledge/*.json before measuring.
if (options.Versions.Contains(AgentVersion.V4))
{
    var index = await host.Services.GetRequiredService<QdrantKnowledgeStore>().IndexAsync();
    Console.WriteLine($"Knowledge index: {index.Added} added, {index.Updated} updated, {index.Deleted} deleted, {index.Unchanged} unchanged");
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
var questionFile = options.QuestionsPath ?? FindUp("eval/questions.json");
var set = JsonSerializer.Deserialize<QuestionSet>(await File.ReadAllTextAsync(questionFile), json)!;
var questions = set.Questions.Where(q => options.Filter is null || q.Id.Contains(options.Filter, StringComparison.OrdinalIgnoreCase) || q.Category.Equals(options.Filter, StringComparison.OrdinalIgnoreCase)).ToList();

Console.WriteLine($"Evaluating {questions.Count} question(s) × {string.Join(", ", options.Versions)} with {model}\n");

var results = new List<Scored>();
foreach (var q in questions)
{
    QueryResult? gold = null;
    if (q.Expect == Expectation.Answer)
        gold = await executor.ExecuteAsync(q.Gold!, new QueryLimits(10_000, 60));

    foreach (var v in options.Versions)
    {
        var clock = Stopwatch.StartNew();
        AskResult? r = null;
        string? error = null;
        try
        {
            r = await agent.AskAsync(q.Question, v);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            error = ex.Message;
        }
        var scored = Score(q, v, r, gold, error, clock.Elapsed, schema);
        results.Add(scored);
        Console.WriteLine($"  {Mark(scored.Verdict)} {v} {q.Id,-32} {scored.Verdict,-14} {scored.Seconds,5:0.0}s  {scored.Detail}");
    }
}

var report = Report.Build(results, questions, options.Versions, model, set, DateTimeOffset.Now);
Console.WriteLine();
Console.WriteLine(report.Summary);

var outDir = options.OutDir ?? Path.Combine(Path.GetDirectoryName(questionFile)!, "results");
Directory.CreateDirectory(outDir);
var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
var setName = Path.GetFileNameWithoutExtension(questionFile);
var baseName = $"{stamp}-{setName}-{model.Replace(':', '-').Replace('/', '-')}";
await File.WriteAllTextAsync(Path.Combine(outDir, baseName + ".md"), report.Markdown);
await File.WriteAllTextAsync(Path.Combine(outDir, baseName + ".json"), JsonSerializer.Serialize(results, json));
Console.WriteLine($"Report: {Path.Combine(outDir, baseName + ".md")}");
return 0;

static Scored Score(EvalQuestion q, AgentVersion v, AskResult? r, QueryResult? gold, string? error, TimeSpan elapsed, DatabaseSchema schema)
{
    var seconds = Math.Round(elapsed.TotalSeconds, 2);
    if (r is null)
        return new Scored(q.Id, q.Category, v, Verdict.Failed, error ?? "no result", null, seconds, 0, false, 0);

    // Did V1/V2 execute SQL that V3 would have refused on security grounds? (Only the database stopped it.)
    var unsafeSql = v < AgentVersion.V3 && r.Attempts.Any(a => a.Sql is not null && SqlValidator.Validate(a.Sql, schema).IsSecurityViolation);

    Scored Make(Verdict verdict, string detail) => new(q.Id, q.Category, v, verdict, detail, r.Sql, seconds, r.Attempts.Count, unsafeSql, r.ToolCalls?.Count ?? 0);

    if (q.Expect == Expectation.Decline)
    {
        return r.Outcome switch
        {
            AskOutcome.NotAnswerable => Make(Verdict.Correct, "declined: " + r.Message),
            AskOutcome.Answered => Make(Verdict.SilentlyWrong, "answered a question it should have declined"),
            _ => Make(Verdict.Failed, $"{r.Outcome} instead of declining: {r.Message}"),
        };
    }

    return r.Outcome switch
    {
        AskOutcome.Answered => ResultComparer.Compare(gold!.Rows, r.Result!.Rows, q.Ordered, q.Absolute) is { IsMatch: true }
            ? Make(Verdict.Correct, $"{r.Result.Rows.Count} row(s)")
            : Make(Verdict.SilentlyWrong, ResultComparer.Compare(gold.Rows, r.Result.Rows, q.Ordered, q.Absolute).Reason),
        AskOutcome.NotAnswerable => Make(Verdict.WronglyDeclined, r.Message ?? ""),
        _ => Make(Verdict.Failed, $"{r.Outcome}: {r.Message}"),
    };
}

static string Mark(Verdict v) => v == Verdict.Correct ? "✓" : v == Verdict.SilentlyWrong ? "✗" : "·";

static string FindUp(string relative)
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, relative);
        if (File.Exists(candidate))
            return candidate;
    }
    throw new FileNotFoundException($"Could not find {relative} above {AppContext.BaseDirectory}.");
}

internal enum Expectation
{
    Answer,
    Decline,
}

internal enum Verdict
{
    Correct,
    SilentlyWrong,
    WronglyDeclined,
    Failed,
}

internal sealed record EvalQuestion(string Id, string Category, string Question, Expectation Expect, string? Gold, bool Ordered, bool Absolute, string? Notes);

internal sealed record QuestionSet(string Description, IReadOnlyList<EvalQuestion> Questions);

internal sealed record Scored(string Id, string Category, AgentVersion Version, Verdict Verdict, string Detail, string? Sql, double Seconds, int Attempts, bool UnsafeSqlExecuted, int ToolCalls = 0);

/// <param name="Model">Run every version on this Ollama model (overrides Llm:Model and Agent:ToolModel), for like-for-like comparisons.</param>
internal sealed record EvalOptions(IReadOnlyList<AgentVersion> Versions, string? Filter, string? QuestionsPath, string? OutDir, string? Model)
{
    public static EvalOptions Parse(string[] args)
    {
        string? Value(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var versions = (Value("--versions") ?? "V1,V2,V3,V4,V5")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => Enum.Parse<AgentVersion>(v, ignoreCase: true))
            .ToList();
        return new EvalOptions(versions, Value("--filter"), Value("--questions"), Value("--out"), Value("--model"));
    }
}

internal sealed record Report(string Summary, string Markdown)
{
    public static Report Build(List<Scored> results, List<EvalQuestion> questions, IReadOnlyList<AgentVersion> versions, string model, QuestionSet set, DateTimeOffset when)
    {
        static string Pct(int n, int of) => of == 0 ? "—" : (100.0 * n / of).ToString("0", CultureInfo.InvariantCulture) + "%";

        var summary = new StringBuilder();
        var md = new StringBuilder();
        md.Append("# Evaluation — ").Append(when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\n\n");
        md.Append("Model: `").Append(model).Append("` · ").Append(questions.Count).Append(" questions (")
          .Append(questions.Count(q => q.Expect == Expectation.Decline)).Append(" should be declined) · scored by execution accuracy against gold SQL.\n\n");

        md.Append("| Version | Correct | Silently wrong | Wrongly declined | Failed | Unsafe SQL executed | Avg time | Avg attempts | Avg tool calls |\n");
        md.Append("|---|---|---|---|---|---|---|---|---|\n");
        summary.Append(CultureInfo.InvariantCulture, $"{"Version",-8}{"Correct",-14}{"Silently wrong",-16}{"Wrongly declined",-18}{"Failed",-8}{"Unsafe SQL",-12}{"Avg s",-7}\n");
        foreach (var v in versions)
        {
            var r = results.Where(x => x.Version == v).ToList();
            int C(Verdict verdict) => r.Count(x => x.Verdict == verdict);
            var avg = r.Count == 0 ? 0 : r.Average(x => x.Seconds);
            var attempts = r.Count == 0 ? 0 : r.Average(x => x.Attempts);
            md.Append(CultureInfo.InvariantCulture, $"| {v} | **{C(Verdict.Correct)}/{r.Count} ({Pct(C(Verdict.Correct), r.Count)})** | {C(Verdict.SilentlyWrong)} | {C(Verdict.WronglyDeclined)} | {C(Verdict.Failed)} | {r.Count(x => x.UnsafeSqlExecuted)} | {avg:0.0} s | {attempts:0.00} | {(r.Count == 0 ? 0 : r.Average(x => x.ToolCalls)):0.00} |\n");
            summary.Append(CultureInfo.InvariantCulture, $"{v,-8}{$"{C(Verdict.Correct)}/{r.Count} ({Pct(C(Verdict.Correct), r.Count)})",-14}{C(Verdict.SilentlyWrong),-16}{C(Verdict.WronglyDeclined),-18}{C(Verdict.Failed),-8}{r.Count(x => x.UnsafeSqlExecuted),-12}{avg,-7:0.0}\n");
        }

        md.Append("\n## By category\n\n| Category | Questions | ").AppendJoin(" | ", versions).Append(" |\n|---|---|").Append(string.Concat(versions.Select(_ => "---|"))).Append('\n');
        foreach (var cat in questions.Select(q => q.Category).Distinct())
        {
            var n = questions.Count(q => q.Category == cat);
            md.Append(CultureInfo.InvariantCulture, $"| {cat} | {n} | ");
            md.AppendJoin(" | ", versions.Select(v => $"{results.Count(x => x.Version == v && x.Category == cat && x.Verdict == Verdict.Correct)}/{n}"));
            md.Append(" |\n");
        }

        md.Append("\n## Per question\n\n| Question | ").AppendJoin(" | ", versions).Append(" |\n|---|").Append(string.Concat(versions.Select(_ => "---|"))).Append('\n');
        foreach (var q in questions)
        {
            md.Append("| `").Append(q.Id).Append("` | ");
            md.AppendJoin(" | ", versions.Select(v =>
            {
                var r = results.FirstOrDefault(x => x.Id == q.Id && x.Version == v);
                return r is null ? "—" : r.Verdict switch
                {
                    Verdict.Correct => "✅",
                    Verdict.SilentlyWrong => "❌ wrong",
                    Verdict.WronglyDeclined => "⚠️ declined",
                    _ => "⚠️ failed",
                } + (r.UnsafeSqlExecuted ? " 🛑" : "");
            }));
            md.Append(" |\n");
        }

        md.Append("\n## Failures in detail\n\n");
        foreach (var r in results.Where(x => x.Verdict != Verdict.Correct))
        {
            md.Append(CultureInfo.InvariantCulture, $"- **{r.Version} · `{r.Id}`** — {r.Verdict}: {r.Detail.Replace("\n", " ", StringComparison.Ordinal)}\n");
            if (r.Sql is not null)
                md.Append("  ```sql\n  ").Append(r.Sql.Replace("\n", "\n  ", StringComparison.Ordinal)).Append("\n  ```\n");
        }

        var noted = questions.Where(q => q.Notes is not null).ToList();
        if (noted.Count > 0)
        {
            md.Append("\n## Scoring decisions\n\n");
            foreach (var q in noted)
                md.Append("- `").Append(q.Id).Append("`: ").Append(q.Notes).Append('\n');
        }
        md.Append("\n_").Append(set.Description).Append("_\n");
        md.Append("\n🛑 = the version executed SQL that V3's validator refuses on security grounds (the database's permissions were the only barrier).\n");

        return new Report(summary.ToString(), md.ToString());
    }
}

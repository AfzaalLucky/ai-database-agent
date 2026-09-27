using System.Diagnostics;
using System.Globalization;
using System.Text;
using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Knowledge;
using AiDatabaseAgent.Core.Observability;
using AiDatabaseAgent.Core.Tools;
using AiDatabaseAgent.Core.Prompting;
using AiDatabaseAgent.Core.Schema;
using AiDatabaseAgent.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDatabaseAgent.Core.Agent;

/// <summary>
/// Answers a natural-language question by generating SQL with a language model and running it.
/// <list type="bullet">
///   <item>V1 — names-only prompt; the SQL is executed as returned.</item>
///   <item>V2 — schema-aware prompt; the model may decline; the SQL is executed as returned.</item>
///   <item>V3 — V2's prompt, then every query is parsed and checked against an allow-list and the schema,
///         cost-estimated, and only then executed. Validation and database errors are fed back to the
///         model for up to <see cref="AgentOptions.MaxRepairAttempts"/> repair rounds.</item>
/// </list>
/// In every version the query runs as the database's least-privilege agent user (the safety floor).
/// </summary>
public sealed partial class TextToSqlAgent(
    IChatClient chat,
    ISchemaProvider schemaProvider,
    IQueryExecutor executor,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<TextToSqlAgent> logger,
    IKnowledgeRetriever? knowledgeRetriever = null)
{
    private readonly AgentOptions _options = options.Value;

    public async Task<AskResult> AskAsync(string question, AgentVersion? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        question = question.Trim();
        if (question.Length > _options.MaxQuestionLength)
            throw new ArgumentException($"Questions are limited to {_options.MaxQuestionLength} characters.", nameof(question));

        var v = version ?? _options.DefaultVersion;
        using var activity = AgentTelemetry.Source.StartActivity("agent.ask");
        activity?.SetTag(AgentTelemetry.Version, v.ToString());
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await AskCoreAsync(question, v, cancellationToken);
            RecordQuestion(activity, v, result.Outcome.ToString(), clock.Elapsed, result.Attempts.Count);
            activity?.SetTag("gen_ai.request.model", result.Model);
            activity?.SetTag("agent.attempts", result.Attempts.Count);
            if (result.Result is { } rows)
                activity?.SetTag("db.response.returned_rows", rows.Rows.Count);
            if (result.Outcome is AskOutcome.Rejected or AskOutcome.Failed)
                activity?.SetStatus(ActivityStatusCode.Error, result.Message);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Infrastructure failures (model, knowledge base or database unreachable) are not agent outcomes.
            RecordQuestion(activity, v, "Error", clock.Elapsed, attempts: null);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            throw;
        }
    }

    private static void RecordQuestion(Activity? activity, AgentVersion version, string outcome, TimeSpan elapsed, int? attempts)
    {
        activity?.SetTag(AgentTelemetry.Outcome, outcome);
        var tags = new TagList { AgentTelemetry.VersionTag(version), { AgentTelemetry.Outcome, outcome } };
        AgentTelemetry.Questions.Add(1, tags);
        AgentTelemetry.QuestionDuration.Record(elapsed.TotalSeconds, tags);
        if (attempts is > 0)
            AgentTelemetry.Attempts.Record(attempts.Value, AgentTelemetry.VersionTag(version));
    }

    private async Task<AskResult> AskCoreAsync(string question, AgentVersion v, CancellationToken cancellationToken)
    {
        var defaultModel = chat.GetService<ChatClientMetadata>()?.DefaultModelId ?? "unknown";
        var run = new Run(question, v, Stopwatch.StartNew(), v == AgentVersion.V5 && _options.ToolModel is { } tm ? tm : defaultModel);
        var schema = await schemaProvider.GetSchemaAsync(cancellationToken);
        run.Knowledge = await RetrieveAsync(v, question, cancellationToken);
        var messages = BuildPrompt(v, schema, question, run.Knowledge);

        // V5: the model calls tools in a loop (FunctionInvokingChatClient) before it answers.
        AgentToolbox? toolbox = null;
        IChatClient client = chat;
        var chatOptions = new ChatOptions { Temperature = 0 };
        if (v == AgentVersion.V5)
        {
            toolbox = new AgentToolbox(schema, executor, knowledgeRetriever, _options.MaxEstimatedCost, _options.QueryTimeoutSeconds)
            {
                // Retrieved up front (full context): a NOT TRACKED entry already justifies declining.
                GlossaryReportedNotTracked = run.Knowledge?.Definitions.Any(h => h.Entry.Kind == KnowledgeKind.NotTracked) ?? false,
            };
            run.Toolbox = toolbox;
            client = new FunctionInvokingChatClient(chat)
            {
                MaximumIterationsPerRequest = _options.MaxToolIterations,
                IncludeDetailedErrors = true,
                AllowConcurrentInvocation = false,
            };
            chatOptions.Tools = toolbox.AsTools();
            chatOptions.ToolMode = ChatToolMode.Auto;
            if (_options.ToolModel is not null)
                chatOptions.ModelId = _options.ToolModel;
        }
        var maxAttempts = v >= AgentVersion.V3 ? 1 + Math.Max(0, _options.MaxRepairAttempts) : 1;

        // Set when the last problem was fixable — a syntax error, a compile/runtime error, or columns taken
        // from the wrong table (the data provably exists). A refusal right after such a problem is not a
        // real "the data isn't there": the model gets one more turn. Refusals after missing data or a
        // security rejection are accepted.
        FixablePush? pushBack = null;

        for (var attempt = 1; ; attempt++)
        {
            var modelClock = Stopwatch.StartNew();
            var response = await client.GetResponseAsync(messages, chatOptions, cancellationToken);
            if (toolbox is not null)
                messages.AddRange(response.Messages); // keep the tool calls and results in the conversation
            var modelElapsed = modelClock.Elapsed;
            AgentTelemetry.RecordStage("model", v, modelElapsed);
            RecordTokens(v, response.Usage);
            var parsed = ModelResponseParser.Parse(response.Text);

            // V5 self-check reply: "CONFIRMED" keeps the query that was just test-run.
            if (run.CandidateSql is not null && parsed.Sql is null && parsed.CannotAnswerReason is null
                && response.Text.Contains("CONFIRMED", StringComparison.OrdinalIgnoreCase))
                parsed = parsed with { Sql = run.CandidateSql, Explanation = run.Explanation };
            run.CandidateSql = null;
            run.Explanation = parsed.Explanation ?? run.Explanation;
            var canRepair = attempt < maxAttempts;

            void Record(string? sql, IReadOnlyList<string> issues) =>
                run.Attempts.Add(new AgentAttempt(attempt, response.Text, sql, issues, modelElapsed, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount));

            // V1 has no refusal protocol; V2+ are told they may decline.
            if (v >= AgentVersion.V2 && parsed.CannotAnswerReason is not null)
            {
                // V5: declining right after a failed test query, without the glossary saying the data isn't
                // tracked, usually means the model gave up on a detail (a guessed column name).
                // Declining without having looked at anything (and with no NOT TRACKED entry to justify it).
                if (pushBack is null && toolbox is { Calls.Count: 0, GlossaryReportedNotTracked: false } && !run.PushedBackUnlooked)
                {
                    run.PushedBackUnlooked = true;
                    pushBack = new FixablePush(
                        "Declined without investigating.",
                        ["You declined without checking the database or the glossary."],
                        "Before concluding the data does not exist, check: search_glossary for the business terms, describe_table for the tables that might hold it. Then answer with a query, or decline if the data really is not there.");
                }
                if (pushBack is null && toolbox is { RefusalLooksPremature: true })
                    pushBack = new FixablePush(
                        "Declined after a failed test query, but the glossary never said the data is missing.",
                        [$"Your last run_query failed: {toolbox.Calls.Last(c => c.Name == "run_query").Result}"],
                        "That was a problem in the query, not proof the data is missing. Call describe_table for each table you used, fix the query and test it with run_query. Decline only if the data really is not in the schema.");

                if (pushBack is not null && canRepair)
                {
                    Record(null, [pushBack.Verdict]);
                    AddRepairTurn(messages, response.Text, [.. pushBack.Problems, pushBack.Instruction]);
                    pushBack = null;
                    continue;
                }
                Record(null, []);
                return Finish(run, AskOutcome.NotAnswerable, null, null, parsed.CannotAnswerReason, null);
            }

            if (parsed.Sql is null)
            {
                const string noSql = "No SQL query was found in the reply.";
                Record(null, [noSql]);
                if (canRepair)
                {
                    AddRepairTurn(messages, response.Text, [noSql]);
                    continue;
                }
                return Finish(run, AskOutcome.Failed, null, null, "The model did not return a SQL query.", null);
            }

            var sql = parsed.Sql;
            QueryCostEstimate? estimate = null;
            if (v >= AgentVersion.V3)
            {
                var validation = Validate(sql, schema, v);
                pushBack = FixablePush.From(validation);
                if (!validation.IsValid)
                {
                    var problems = validation.Issues.Select(i => i.Message).ToList();
                    Record(sql, problems);
                    if (validation.IsSecurityViolation)
                        LogSecurityRejection(logger, string.Join(" ", problems));
                    if (canRepair)
                    {
                        AddRepairTurn(messages, response.Text, problems);
                        continue;
                    }
                    return Finish(run, AskOutcome.Rejected, sql, null, "The generated query did not pass validation: " + string.Join(" ", problems), null);
                }

                try
                {
                    estimate = await EstimateAsync(sql, v, cancellationToken);
                }
                catch (QueryExecutionException ex)
                {
                    Record(sql, [ex.Message]);
                    if (canRepair)
                    {
                        var problem = $"SQL Server could not compile the query: {ex.Message}";
                        pushBack = FixablePush.ForError(problem);
                        AddRepairTurn(messages, response.Text, [problem]);
                        continue;
                    }
                    return Finish(run, AskOutcome.Failed, sql, null, ex.Message, null);
                }

                if (estimate.EstimatedCost > _options.MaxEstimatedCost)
                {
                    var tooCostly = string.Create(CultureInfo.InvariantCulture,
                        $"The query's estimated cost ({estimate.EstimatedCost:0.##}) exceeds the limit ({_options.MaxEstimatedCost:0.##}).");
                    Record(sql, [tooCostly]);
                    return Finish(run, AskOutcome.Rejected, sql, null, tooCostly + " Try a narrower question (a shorter period, fewer groups).", estimate);
                }
            }

            // V5: test-run the answer and let the model confirm or correct it before the final execution.
            if (toolbox is not null && _options.V5SelfCheck && !run.SelfChecked)
            {
                run.SelfChecked = true;
                run.CandidateSql = sql;
                var preview = await toolbox.RunQuery(sql, cancellationToken);
                Record(sql, []);
                messages.Add(new ChatMessage(ChatRole.User, SelfCheckPrompt(question, preview)));
                maxAttempts++; // verification does not use the repair budget
                continue;
            }

            try
            {
                var result = await ExecuteAsync(sql, v, cancellationToken);
                Record(sql, []);
                return Finish(run, AskOutcome.Answered, sql, result, null, estimate);
            }
            catch (QueryExecutionException ex)
            {
                Record(sql, [ex.Message]);
                // A timeout will not be fixed by rephrasing the same query; everything else is worth one more try.
                if (canRepair && ex.ErrorNumber != -2)
                {
                    var problem = $"SQL Server returned an error: {ex.Message}";
                    pushBack = FixablePush.ForError(problem);
                    AddRepairTurn(messages, response.Text, [problem]);
                    continue;
                }
                return Finish(run, AskOutcome.Failed, sql, null, ex.Message, estimate);
            }
        }
    }

    /// <summary>The exact messages a version sends for a question (shown in the UI and used by tests).</summary>
    public async Task<IReadOnlyList<ChatMessage>> PreviewPromptAsync(string question, AgentVersion version, CancellationToken cancellationToken = default) =>
        BuildPrompt(version, await schemaProvider.GetSchemaAsync(cancellationToken), question, await RetrieveAsync(version, question, cancellationToken));

    private async Task<KnowledgeContext?> RetrieveAsync(AgentVersion version, string question, CancellationToken cancellationToken)
    {
        if (version < AgentVersion.V4 || (version == AgentVersion.V5 && _options.V5Context == V5Context.Minimal))
            return null; // V1–V3 don't use the glossary; minimal V5 searches it itself through a tool
        if (knowledgeRetriever is null)
            throw new NotSupportedException("V4 needs the knowledge base, which is not configured.");

        using var activity = AgentTelemetry.Source.StartActivity("agent.retrieve");
        var clock = Stopwatch.StartNew();
        var knowledge = await knowledgeRetriever.RetrieveAsync(question, cancellationToken);
        AgentTelemetry.RecordStage("retrieval", version, clock.Elapsed);
        activity?.SetTag("agent.knowledge.definitions", knowledge.Definitions.Count);
        activity?.SetTag("agent.knowledge.examples", knowledge.Examples.Count);
        activity?.SetTag("agent.knowledge.ids", string.Join(",", knowledge.Definitions.Select(d => d.Entry.Id)));
        return knowledge;
    }

    private static ValidationResult Validate(string sql, DatabaseSchema schema, AgentVersion version)
    {
        using var activity = AgentTelemetry.Source.StartActivity("agent.validate");
        var clock = Stopwatch.StartNew();
        var validation = SqlValidator.Validate(sql, schema);
        AgentTelemetry.RecordStage("validation", version, clock.Elapsed);
        activity?.SetTag("db.query.text", sql);
        activity?.SetTag("agent.validation.valid", validation.IsValid);
        if (!validation.IsValid)
        {
            activity?.SetTag("agent.validation.issues", string.Join(" | ", validation.Issues.Select(i => i.Message)));
            foreach (var issue in validation.Issues)
                AgentTelemetry.ValidationIssues.Add(1,
                    new KeyValuePair<string, object?>("agent.issue.kind", issue.Kind.ToString()),
                    new KeyValuePair<string, object?>("agent.security", validation.IsSecurityViolation));
        }
        return validation;
    }

    private async Task<QueryCostEstimate> EstimateAsync(string sql, AgentVersion version, CancellationToken cancellationToken)
    {
        using var activity = StartDbActivity("db.estimate", sql);
        var clock = Stopwatch.StartNew();
        try
        {
            var estimate = await executor.EstimateAsync(sql, cancellationToken);
            activity?.SetTag("agent.estimate.cost", estimate.EstimatedCost);
            activity?.SetTag("agent.estimate.rows", estimate.EstimatedRows);
            return estimate;
        }
        catch (QueryExecutionException ex)
        {
            SetDbError(activity, ex);
            throw;
        }
        finally
        {
            AgentTelemetry.RecordStage("estimate", version, clock.Elapsed);
        }
    }

    private async Task<QueryResult> ExecuteAsync(string sql, AgentVersion version, CancellationToken cancellationToken)
    {
        using var activity = StartDbActivity("db.execute", sql);
        var clock = Stopwatch.StartNew();
        try
        {
            var result = await executor.ExecuteAsync(sql, _options.Limits, cancellationToken);
            activity?.SetTag("db.response.returned_rows", result.Rows.Count);
            activity?.SetTag("agent.result.truncated", result.Truncated);
            return result;
        }
        catch (QueryExecutionException ex)
        {
            SetDbError(activity, ex);
            throw;
        }
        finally
        {
            AgentTelemetry.RecordStage("execution", version, clock.Elapsed);
        }
    }

    private static Activity? StartDbActivity(string name, string sql)
    {
        var activity = AgentTelemetry.Source.StartActivity(name, ActivityKind.Client);
        activity?.SetTag("db.system.name", "microsoft.sql_server");
        activity?.SetTag("db.query.text", sql);
        return activity;
    }

    private static void SetDbError(Activity? activity, QueryExecutionException ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        if (ex.ErrorNumber is { } number)
            activity?.SetTag("db.response.status_code", number.ToString(CultureInfo.InvariantCulture));
    }

    private static void RecordTokens(AgentVersion version, UsageDetails? usage)
    {
        if (usage?.InputTokenCount is { } input)
            AgentTelemetry.Tokens.Add(input, AgentTelemetry.VersionTag(version), new KeyValuePair<string, object?>("gen_ai.token.type", "input"));
        if (usage?.OutputTokenCount is { } output)
            AgentTelemetry.Tokens.Add(output, AgentTelemetry.VersionTag(version), new KeyValuePair<string, object?>("gen_ai.token.type", "output"));
    }

    private List<ChatMessage> BuildPrompt(AgentVersion version, DatabaseSchema schema, string question, KnowledgeContext? knowledge) => version switch
    {
        AgentVersion.V1 => BasicPromptBuilder.Build(schema, question),
        AgentVersion.V2 or AgentVersion.V3 => SchemaAwarePromptBuilder.Build(schema, question, _options.DomainNotes, _options.Examples, time.GetUtcNow()),
        AgentVersion.V4 => SchemaAwarePromptBuilder.Build(schema, question, _options.DomainNotes, _options.Examples, time.GetUtcNow(), knowledge),
        AgentVersion.V5 when _options.V5Context == V5Context.Minimal => AgentPromptBuilder.Build(schema, question, _options.DomainNotes, time.GetUtcNow()),
        AgentVersion.V5 => AgentPromptBuilder.WithTools(SchemaAwarePromptBuilder.Build(schema, question, _options.DomainNotes, _options.Examples, time.GetUtcNow(), knowledge)),
        _ => throw new NotSupportedException($"Agent version {version} is not supported."),
    };

    private static string SelfCheckPrompt(string question, string preview) => $"""
        I ran your query (run_query). It returned:
        {preview}

        Check that this answers the question "{question}" exactly: the right grain (what does one row represent?),
        the right number of rows (a single number, a top-N list, one row per group?), the right units, and the business
        definitions above. If it does, reply with just CONFIRMED. If not, reply with one short sentence and the corrected
        query in a ```sql block.
        """;

    /// <summary>Feeds the model its own reply and what was wrong with it, and asks for a fix — or a refusal.</summary>
    private static void AddRepairTurn(List<ChatMessage> messages, string reply, IReadOnlyList<string> problems)
    {
        var replyInHistory = messages.Count > 0 && messages[^1].Role == ChatRole.Assistant && messages[^1].Text == reply;
        var sb = new StringBuilder("That query was not accepted:\n");
        foreach (var p in problems)
            sb.Append("- ").Append(p).Append('\n');
        sb.Append("\nWrite a corrected query using only tables and columns from the schema. ")
          .Append("If a column was taken from the wrong table, take it from the table that has it (joining it if needed). ")
          .Append("Reply with CANNOT_ANSWER: <reason> only if the data needed does not exist anywhere in the schema.");
        if (!replyInHistory)
            messages.Add(new ChatMessage(ChatRole.Assistant, reply));
        messages.Add(new ChatMessage(ChatRole.User, sb.ToString()));
    }

    private AskResult Finish(Run run, AskOutcome outcome, string? sql, QueryResult? result, string? message, QueryCostEstimate? estimate)
    {
        LogAsked(logger, run.Version, outcome, run.Clock.ElapsedMilliseconds, run.Attempts.Count);
        return new AskResult(run.Question, run.Version, run.Model, outcome, sql, run.Explanation, result, message, run.Attempts, estimate, run.Clock.Elapsed, run.Knowledge, run.Toolbox?.Calls.ToList());
    }

    /// <summary>A problem the model can fix, and what to tell it if it tries to decline instead.</summary>
    private sealed record FixablePush(string Verdict, IReadOnlyList<string> Problems, string Instruction)
    {
        /// <summary>Only syntax errors and wrong-table columns are fixable; missing data and security findings are not.</summary>
        public static FixablePush? From(ValidationResult validation)
        {
            if (validation.IsValid)
                return null;
            var problems = validation.Issues.Select(i => i.Message).ToList();
            if (validation.Issues.All(i => i.Kind == IssueKind.UnknownColumn && i.FoundOn is not null))
                return new FixablePush(
                    $"Declined, but the data does exist ({string.Join("; ", validation.Issues.Select(i => i.FoundOn))}).",
                    problems,
                    "The data needed DOES exist, so do not decline: rewrite the query taking each column from the table that has it.");
            if (validation.Issues.All(i => i.Kind == IssueKind.Syntax))
                return ForError(string.Join(" ", problems));
            return null;
        }

        public static FixablePush ForError(string problem) => new(
            "Declined after a fixable error.",
            [problem],
            "This was an error in the SQL, not missing data, so do not decline: write a corrected query.");
    }

    private sealed class Run(string question, AgentVersion version, Stopwatch clock, string model)
    {
        public string Question { get; } = question;
        public AgentVersion Version { get; } = version;
        public Stopwatch Clock { get; } = clock;
        public string Model { get; } = model;
        public string? Explanation { get; set; }
        public KnowledgeContext? Knowledge { get; set; }
        public AgentToolbox? Toolbox { get; set; }
        public bool PushedBackUnlooked { get; set; }
        public bool SelfChecked { get; set; }
        public string? CandidateSql { get; set; }
        public List<AgentAttempt> Attempts { get; } = [];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Asked ({Version}): {Outcome} in {ElapsedMs} ms after {Attempts} attempt(s)")]
    private static partial void LogAsked(ILogger logger, AgentVersion version, AskOutcome outcome, long elapsedMs, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused generated SQL on security grounds: {Problems}")]
    private static partial void LogSecurityRejection(ILogger logger, string problems);
}

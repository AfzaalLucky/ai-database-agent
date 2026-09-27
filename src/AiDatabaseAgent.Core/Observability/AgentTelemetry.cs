using System.Diagnostics;
using System.Diagnostics.Metrics;
using AiDatabaseAgent.Core.Agent;

namespace AiDatabaseAgent.Core.Observability;

/// <summary>
/// Traces and metrics for the agent pipeline, on the BCL's <see cref="ActivitySource"/> and <see cref="Meter"/>:
/// Core stays free of any exporter or SDK, and the host decides where the data goes (OpenTelemetry in the API).
/// <para>
/// Spans: <c>agent.ask</c> (one per question) with children <c>agent.retrieve</c>, <c>agent.validate</c>,
/// <c>db.estimate</c> and <c>db.execute</c>. Model calls and tool calls get their own spans from
/// Microsoft.Extensions.AI's OpenTelemetry client, which nest under <c>agent.ask</c>.
/// </para>
/// <para>
/// Question text is never put on spans or metric tags (it can contain anything a user typed); the SQL the
/// agent generated is, because it is what an operator needs to debug a slow or failed query.
/// </para>
/// </summary>
public static class AgentTelemetry
{
    public const string SourceName = "AiDatabaseAgent";

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    /// <summary>Questions answered, by version and outcome.</summary>
    public static readonly Counter<long> Questions = Meter.CreateCounter<long>(
        "agent.questions", unit: "{question}", description: "Questions handled, by agent version and outcome.");

    /// <summary>End-to-end time per question, by version and outcome.</summary>
    public static readonly Histogram<double> QuestionDuration = Meter.CreateHistogram<double>(
        "agent.question.duration", unit: "s", description: "Time to answer a question, end to end.");

    /// <summary>Time per pipeline stage (retrieval, model, validation, estimate, execution).</summary>
    public static readonly Histogram<double> StageDuration = Meter.CreateHistogram<double>(
        "agent.stage.duration", unit: "s", description: "Time spent in one pipeline stage.");

    /// <summary>Model round trips per question: 1 means no repair was needed.</summary>
    public static readonly Histogram<int> Attempts = Meter.CreateHistogram<int>(
        "agent.attempts", unit: "{attempt}", description: "Model round trips needed per question.");

    /// <summary>Validator findings, by issue kind; <c>agent.security</c> marks allow-list (security) findings.</summary>
    public static readonly Counter<long> ValidationIssues = Meter.CreateCounter<long>(
        "agent.validation.issues", unit: "{issue}", description: "Problems the SQL validator found in generated queries.");

    /// <summary>Tokens per model call, by direction (input/output).</summary>
    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        "agent.tokens", unit: "{token}", description: "Model tokens used, by direction.");

    internal const string Version = "agent.version";
    internal const string Outcome = "agent.outcome";
    internal const string Stage = "agent.stage";

    internal static KeyValuePair<string, object?> VersionTag(AgentVersion version) => new(Version, version.ToString());

    /// <summary>Records one stage's duration.</summary>
    internal static void RecordStage(string stage, AgentVersion version, TimeSpan elapsed) =>
        StageDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>(Stage, stage), VersionTag(version));
}

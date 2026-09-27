using AiDatabaseAgent.Core.Execution;
using AiDatabaseAgent.Core.Knowledge;
using AiDatabaseAgent.Core.Tools;

namespace AiDatabaseAgent.Core.Agent;

/// <summary>
/// The agent is built and documented version by version; every version stays runnable so the same
/// question can be compared across them.
/// </summary>
public enum AgentVersion
{
    /// <summary>Text-to-SQL: table and column names only, the model's SQL is executed as-is (on the database safety floor).</summary>
    V1 = 1,

    /// <summary>Schema-aware: types, keys, join paths, descriptions, allowed values, dialect rules and examples; may decline.</summary>
    V2 = 2,

    /// <summary>Secured and validated: V2 plus a T-SQL parser allow-list, a cost check and error-driven repair.</summary>
    V3 = 3,

    /// <summary>Knowledge-augmented: V3 plus business definitions and verified examples retrieved per question (RAG).</summary>
    V4 = 4,

    /// <summary>Agent: the model explores with tools (describe tables, search the glossary, sample values, test queries), then V3's gate.</summary>
    V5 = 5,
}

public enum AskOutcome
{
    /// <summary>A query ran and returned a result.</summary>
    Answered,

    /// <summary>The model said the question can't be answered from the available data.</summary>
    NotAnswerable,

    /// <summary>The generated SQL was refused by validation or the cost limit and could not be repaired.</summary>
    Rejected,

    /// <summary>The model produced no usable SQL, or the query failed on the database.</summary>
    Failed,
}

/// <summary>One model round-trip and what happened to its SQL.</summary>
public sealed record AgentAttempt(
    int Number,
    string ModelResponse,
    string? Sql,
    IReadOnlyList<string> Issues,
    TimeSpan ModelElapsed,
    long? InputTokens,
    long? OutputTokens);

public sealed record AskResult(
    string Question,
    AgentVersion Version,
    string Model,
    AskOutcome Outcome,
    string? Sql,
    string? Explanation,
    QueryResult? Result,
    string? Message,
    IReadOnlyList<AgentAttempt> Attempts,
    QueryCostEstimate? Estimate,
    TimeSpan Elapsed,
    KnowledgeContext? Knowledge = null,
    IReadOnlyList<ToolCall>? ToolCalls = null);

/// <summary>A worked example shown to the model (V2+). Keep these out of any evaluation set.</summary>
/// <param name="Explanation">The one-sentence description the model should imitate, e.g. "Counts customers per segment."</param>
public sealed record FewShotExample(string Question, string Sql, string? Explanation = null);

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public AgentVersion DefaultVersion { get; set; } = AgentVersion.V3;

    public int MaxRows { get; set; } = 500;

    public int QueryTimeoutSeconds { get; set; } = 15;

    /// <summary>V3: extra model round-trips allowed to fix SQL that failed validation or execution.</summary>
    public int MaxRepairAttempts { get; set; } = 2;

    /// <summary>V3: queries whose estimated optimizer cost exceeds this are refused before running.</summary>
    public double MaxEstimatedCost { get; set; } = 200;

    /// <summary>V5: the model used for tool calling (it must emit native tool calls; null = the default model).</summary>
    public string? ToolModel { get; set; }

    /// <summary>
    /// V5: what the model starts from. <c>full</c> (default) = V4's context (schema DDL + retrieved definitions)
    /// plus tools to verify; <c>minimal</c> = table names only, everything else through tools (the pure-agent
    /// experiment: 47% vs V4's 88% on the development set with the same model, so not the default).
    /// </summary>
    public V5Context V5Context { get; set; } = V5Context.Full;

    /// <summary>
    /// V5: after the answer passes the V3 gate, the agent itself test-runs it (run_query) and shows the model the
    /// first rows to confirm or correct. Given full context, qwen2.5:7b answered 22 of 22 sampled questions
    /// without calling any tool, and Ollama cannot force a tool call (no "required" tool choice), so the harness
    /// makes the check itself.
    /// </summary>
    public bool V5SelfCheck { get; set; } = true;

    /// <summary>V5: most tool-calling round trips the model may make per turn before it must answer.</summary>
    public int MaxToolIterations { get; set; } = 12;

    /// <summary>Longest question accepted, in characters.</summary>
    public int MaxQuestionLength { get; set; } = 500;

    /// <summary>V2+: facts about the data the schema can't express (e.g. the period it covers).</summary>
    public IList<string> DomainNotes { get; } = [];

    /// <summary>V2+: worked examples included in the prompt.</summary>
    public IList<FewShotExample> Examples { get; } = [];

    public QueryLimits Limits => new(MaxRows, QueryTimeoutSeconds);
}

public enum V5Context
{
    /// <summary>Start from V4's full context; tools are for verifying and looking up extra details.</summary>
    Full,

    /// <summary>Start from table names only; the model must explore everything through tools.</summary>
    Minimal,
}

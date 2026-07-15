namespace AnswerCode.Models;

/// <summary>
/// Tuning knobs for the agent tool-calling loop: deterministic symbol context
/// pre-fetching and question-complexity based iteration budgets.
/// Binds to the "AgentSettings" section in appsettings.json.
/// </summary>
public class AgentSettings
{
    public const string SectionName = "AgentSettings";

    /// <summary>
    /// Enable deterministic call-graph/reference pre-fetch for symbols detected in the question,
    /// injected into the first agent message before the tool loop starts.
    /// </summary>
    public bool EnableSymbolContextExpansion { get; set; } = true;

    /// <summary>
    /// Enable iteration-budget routing based on a rule-based question complexity classification.
    /// When disabled, every question uses <see cref="ComplexQuestionMaxIterations"/>.
    /// </summary>
    public bool EnableComplexityRouting { get; set; } = true;

    /// <summary>
    /// Enable running the tool calls returned in a single LLM turn concurrently instead of
    /// sequentially. Cuts wall-clock latency and encourages fewer round trips when the model
    /// batches independent lookups. The <c>ask_user</c> tool is always excluded and run alone,
    /// since it pauses the run to wait for a human reply.
    /// </summary>
    public bool EnableParallelToolExecution { get; set; } = true;

    /// <summary>Max tool-loop iterations for questions classified as Simple (e.g. "where is X defined").</summary>
    public int SimpleQuestionMaxIterations { get; set; } = 8;

    /// <summary>Max tool-loop iterations for questions classified as Standard (default/ambiguous).</summary>
    public int StandardQuestionMaxIterations { get; set; } = 25;

    /// <summary>Max tool-loop iterations for questions classified as Complex (multi-hop/architecture).</summary>
    public int ComplexQuestionMaxIterations { get; set; } = 50;
}

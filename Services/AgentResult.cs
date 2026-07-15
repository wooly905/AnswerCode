using AnswerCode.Models;

namespace AnswerCode.Services;

/// <summary>
/// Result from an agent run
/// </summary>
public class AgentResult
{
    public string Answer { get; set; } = string.Empty;
    /// <summary>Thinking/reasoning content from the final answer (null when not a thinking model).</summary>
    public string? ThinkingContent { get; set; }
    public List<string> RelevantFiles { get; set; } = [];
    public List<ToolCallRecord> ToolCalls { get; set; } = [];
    public int TotalToolCalls { get; set; }
    public int IterationCount { get; set; }
    /// <summary>Total input (prompt) tokens across all LLM calls in the agent run.</summary>
    public int TotalInputTokens { get; set; }
    /// <summary>Total output (completion) tokens across all LLM calls in the agent run.</summary>
    public int TotalOutputTokens { get; set; }

    /// <summary>Input tokens consumed by the main agent (Phase 1 context resolution + Phase 3 synthesis). Zero when no history.</summary>
    public int MainAgentInputTokens { get; set; }
    /// <summary>Output tokens consumed by the main agent (Phase 1 context resolution + Phase 3 synthesis). Zero when no history.</summary>
    public int MainAgentOutputTokens { get; set; }

    /// <summary>Question complexity classification (Simple/Standard/Complex) used to size this run's iteration budget.</summary>
    public string? ComplexityLabel { get; set; }
    /// <summary>True when deterministic symbol context (call graph/references) was pre-fetched and injected before the tool loop started.</summary>
    public bool UsedPrefetchedContext { get; set; }
}

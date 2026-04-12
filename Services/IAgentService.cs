using AnswerCode.Models;

namespace AnswerCode.Services;

/// <summary>
/// Agent Service Interface — runs an agentic tool-calling loop to answer code questions
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// Run the agent to answer a question about a codebase
    /// </summary>
    Task<AgentResult> RunAsync(string question,
                               string rootPath,
                               string? sessionId = null,
                               string? modelProvider = null,
                               string? userRole = null,
                               List<ConversationTurn>? conversationHistory = null);

    /// <summary>
    /// Run the agent with a progress callback for SSE streaming
    /// </summary>
    Task<AgentResult> RunAsync(string question,
                               string rootPath,
                               Func<AgentEvent, Task> onProgress,
                               string? sessionId = null,
                               string? modelProvider = null,
                               string? userRole = null,
                               List<ConversationTurn>? conversationHistory = null);
}

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
}

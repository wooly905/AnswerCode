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
    Task<AgentResult> RunAsync(string question, string rootPath, string? sessionId = null, string? modelProvider = null, string? userRole = null);

    /// <summary>
    /// Run the agent with a progress callback for SSE streaming
    /// </summary>
    Task<AgentResult> RunAsync(string question, string rootPath,
        Func<AgentEvent, Task> onProgress,
        string? sessionId = null, string? modelProvider = null, string? userRole = null);
}

/// <summary>
/// Result from an agent run
/// </summary>
public class AgentResult
{
    public string Answer { get; set; } = string.Empty;
    public List<string> RelevantFiles { get; set; } = new();
    public List<ToolCallRecord> ToolCalls { get; set; } = new();
    public int TotalToolCalls { get; set; }
    public int IterationCount { get; set; }
    /// <summary>Total input (prompt) tokens across all LLM calls in the agent run.</summary>
    public int TotalInputTokens { get; set; }
    /// <summary>Total output (completion) tokens across all LLM calls in the agent run.</summary>
    public int TotalOutputTokens { get; set; }
}

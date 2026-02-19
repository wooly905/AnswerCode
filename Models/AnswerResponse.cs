namespace AnswerCode.Models;

/// <summary>
/// Answer response
/// </summary>
public class AnswerResponse
{
    /// <summary>
    /// AI answer
    /// </summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>
    /// Relevant files found during search
    /// </summary>
    public List<string> RelevantFiles { get; set; } = new();

    /// <summary>
    /// Processing time (milliseconds)
    /// </summary>
    public long ProcessingTimeMs { get; set; }

    /// <summary>
    /// Session ID
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Total number of tool calls made by the agent
    /// </summary>
    public int ToolCallCount { get; set; }

    /// <summary>
    /// Number of agent iterations (each iteration may have multiple tool calls)
    /// </summary>
    public int IterationCount { get; set; }

    /// <summary>
    /// Detailed record of each tool call made during the agent run
    /// </summary>
    public List<ToolCallRecord> ToolCalls { get; set; } = new();

    /// <summary>
    /// Total input (prompt) tokens consumed across all LLM calls
    /// </summary>
    public int TotalInputTokens { get; set; }

    /// <summary>
    /// Total output (completion) tokens generated across all LLM calls
    /// </summary>
    public int TotalOutputTokens { get; set; }
}
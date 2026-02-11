namespace AnswerCode.Models;

/// <summary>
/// Event types sent via SSE during agent execution
/// </summary>
public enum AgentEventType
{
    /// <summary>Agent has started processing</summary>
    Started,
    /// <summary>A tool call is about to execute</summary>
    ToolCallStart,
    /// <summary>A tool call has completed</summary>
    ToolCallEnd,
    /// <summary>The final answer is ready</summary>
    Answer,
    /// <summary>An error occurred</summary>
    Error
}

/// <summary>
/// An event emitted during agent execution, sent to the UI via SSE
/// </summary>
public class AgentEvent
{
    public AgentEventType Type { get; set; }
    public string? ToolName { get; set; }
    public string? ToolArgs { get; set; }
    public string? Summary { get; set; }
    public long? DurationMs { get; set; }
    public int? Iteration { get; set; }
    public int? TotalToolCalls { get; set; }

    /// <summary>Only set for Answer/Error events</summary>
    public AnswerResponse? Result { get; set; }
}

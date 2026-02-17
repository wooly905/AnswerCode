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

    /// <summary>
    /// Brief human-readable result summary for ToolCallEnd
    /// (e.g. "5 matches in 3 files", "Found 2 definition(s)")
    /// </summary>
    public string? ResultSummary { get; set; }

    /// <summary>
    /// Full tool result text for the expandable detail section in the UI.
    /// Only set for ToolCallEnd events. Truncated to a reasonable size.
    /// </summary>
    public string? ResultDetails { get; set; }

    /// <summary>
    /// Structured detail items for the expandable section (rendered as bullet list).
    /// Each item is a human-readable string (e.g. file path with match count).
    /// Only set for ToolCallEnd events.
    /// </summary>
    public List<string>? DetailItems { get; set; }

    /// <summary>
    /// Optional label for the DetailItems section (e.g. "Matched Files", "Found Files").
    /// </summary>
    public string? DetailLabel { get; set; }

    /// <summary>Only set for Answer/Error events</summary>
    public AnswerResponse? Result { get; set; }
}

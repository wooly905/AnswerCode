namespace AnswerCode.Models;

/// <summary>
/// Record of a single tool call made during agent execution
/// </summary>
public class ToolCallRecord
{
    /// <summary>
    /// Tool name that was called
    /// </summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// Arguments passed to the tool (JSON)
    /// </summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>
    /// Brief summary of the result
    /// </summary>
    public string ResultSummary { get; set; } = string.Empty;

    /// <summary>
    /// Duration of this tool call in milliseconds
    /// </summary>
    public long DurationMs { get; set; }
}

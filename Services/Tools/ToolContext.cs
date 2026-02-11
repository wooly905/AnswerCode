namespace AnswerCode.Services.Tools;

/// <summary>
/// Context passed to tools during execution
/// </summary>
public class ToolContext
{
    /// <summary>
    /// Root path of the project being analyzed
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Logger for tool execution
    /// </summary>
    public ILogger? Logger { get; init; }
}

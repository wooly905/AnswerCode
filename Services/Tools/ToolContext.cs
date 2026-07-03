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

    /// <summary>
    /// Progress callback used to emit <see cref="Models.AgentEvent"/>s (e.g. tool call start/end, UserQuestion) to the UI.
    /// </summary>
    public Func<Models.AgentEvent, Task>? OnProgress { get; init; }

    /// <summary>
    /// Current conversation/session id, used to correlate asynchronous user input requests.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Service used by tools (e.g. <see cref="AskUserTool"/>) to pause execution and wait for a user-supplied answer.
    /// </summary>
    public IUserInputService? UserInputService { get; init; }

    /// <summary>
    /// Resolve a path (absolute or relative) and verify it is confined within <see cref="RootPath"/>.
    /// Returns null if the path escapes the allowed boundary.
    /// </summary>
    public string? ResolvePath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(Path.Combine(RootPath, input));
        string rootFull = Path.GetFullPath(RootPath);

        if (fullPath.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        Logger?.LogWarning("Tool path confinement: blocked access to {Path} (outside {Root})", fullPath, rootFull);
        return null;
    }
}

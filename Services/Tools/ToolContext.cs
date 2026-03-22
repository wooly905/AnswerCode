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
    /// Resolve a path (absolute or relative) and verify it is confined within <see cref="RootPath"/>.
    /// Returns null if the path escapes the allowed boundary.
    /// </summary>
    public string? ResolvePath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var fullPath = Path.GetFullPath(Path.Combine(RootPath, input));
        var rootFull = Path.GetFullPath(RootPath);

        if (fullPath.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return fullPath;

        Logger?.LogWarning("Tool path confinement: blocked access to {Path} (outside {Root})", fullPath, rootFull);
        return null;
    }
}

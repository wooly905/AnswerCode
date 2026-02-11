namespace AnswerCode.Models;

/// <summary>
/// Code search result
/// </summary>
public class SearchResult
{
    /// <summary>
    /// File path
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Line number
    /// </summary>
    public int LineNumber
    {
        get; set;
    }

    /// <summary>
    /// Line content
    /// </summary>
    public string LineContent { get; set; } = string.Empty;

    /// <summary>
    /// Relative path (relative to project root)
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;
}
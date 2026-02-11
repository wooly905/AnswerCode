namespace AnswerCode.Models;

/// <summary>
/// Code context
/// </summary>
public class CodeContext
{
    /// <summary>
    /// File contents dictionary (file path -> content)
    /// </summary>
    public Dictionary<string, string> FileContents { get; set; } = new();

    /// <summary>
    /// Search results
    /// </summary>
    public List<SearchResult> SearchResults { get; set; } = new();

    /// <summary>
    /// Project structure summary
    /// </summary>
    public string ProjectStructure { get; set; } = string.Empty;
}
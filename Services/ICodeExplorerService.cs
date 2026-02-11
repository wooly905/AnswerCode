using AnswerCode.Models;

namespace AnswerCode.Services;

/// <summary>
/// Code Explorer Service Interface
/// </summary>
public interface ICodeExplorerService
{
    /// <summary>
    /// Search file contents using regex (similar to grep)
    /// </summary>
    //public Task<List<SearchResult>> GrepSearchAsync(string pattern,
    //                                                string rootPath,
    //                                                bool caseInsensitive = true,
    //                                                string? include = null);

    /// <summary>
    /// Read file contents
    /// </summary>
    public Task<string> ReadFileAsync(string filePath, int? maxLines = null, int? offset = null);

    /// <summary>
    /// Get project structure overview
    /// </summary>
    public Task<string> GetProjectStructureAsync(string rootPath, int maxDepth = 3);

    /// <summary>
    /// Intelligently search relevant code based on the question
    /// </summary>
    public Task<CodeContext> ExploreCodebaseAsync(string question, string rootPath, string? modelProvider = null);
}

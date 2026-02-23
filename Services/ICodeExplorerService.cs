using AnswerCode.Models;

namespace AnswerCode.Services;

/// <summary>
/// Code Explorer Service Interface
/// </summary>
public interface ICodeExplorerService
{
    /// <summary>
    /// Read file contents
    /// </summary>
    Task<string> ReadFileAsync(string filePath, int? maxLines = null, int? offset = null);

    /// <summary>
    /// Get project structure overview
    /// </summary>
    Task<string> GetProjectStructureAsync(string rootPath, int maxDepth = 3);

    /// <summary>
    /// Intelligently search relevant code based on the question
    /// </summary>
    Task<CodeContext> ExploreCodebaseAsync(string question, string rootPath, string? modelProvider = null);
}

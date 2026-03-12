namespace AnswerCode.Services;

/// <summary>
/// Builds a repository map that identifies module boundaries, architectural roles,
/// cross-module dependencies, and entry points.
/// </summary>
public interface IRepoMapService
{
    /// <summary>
    /// Generate a repository map for the given root path.
    /// </summary>
    /// <param name="rootPath">Project root directory</param>
    /// <param name="scope">Optional subdirectory to limit analysis</param>
    /// <param name="maxDepth">Max folder depth for module detection (default 3)</param>
    /// <param name="includeDependencies">Include cross-module dependency edges</param>
    Task<string> BuildRepoMapAsync(string rootPath, string? scope = null, int maxDepth = 3, bool includeDependencies = true);
}

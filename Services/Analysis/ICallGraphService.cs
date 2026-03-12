namespace AnswerCode.Services.Analysis;

public interface ICallGraphService
{
    /// <summary>
    /// Build a static call graph starting from the given symbol.
    /// </summary>
    /// <param name="rootPath">Project root path</param>
    /// <param name="symbol">Starting symbol name (method, function, etc.)</param>
    /// <param name="filePath">Optional file path to disambiguate the symbol</param>
    /// <param name="depth">Max traversal depth (default 2, max 5)</param>
    /// <param name="direction">"downstream" (callees) or "upstream" (callers)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<CallGraphResult> BuildCallGraphAsync(string rootPath,
                                              string symbol,
                                              string? filePath = null,
                                              int depth = 2,
                                              string direction = "downstream",
                                              CancellationToken cancellationToken = default);
}

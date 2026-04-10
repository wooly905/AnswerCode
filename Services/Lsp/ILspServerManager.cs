namespace AnswerCode.Services.Lsp;

/// <summary>
/// Manages LSP server instances per (rootPath, languageId) pair, with idle eviction.
/// </summary>
public interface ILspServerManager : IAsyncDisposable
{
    /// <summary>
    /// Returns true if there is an LSP server configured for the given language.
    /// </summary>
    bool HasServerFor(string languageId);

    /// <summary>
    /// Get or create an LSP server for the given root path and language.
    /// Returns null if no server is configured for the language or if startup fails.
    /// </summary>
    Task<LspServerHandle?> GetServerAsync(string rootPath, string languageId, CancellationToken ct = default);

    /// <summary>
    /// Evict all servers for a given root path (e.g. when a project is deleted).
    /// </summary>
    Task EvictAsync(string rootPath);
}

/// <summary>
/// Opaque handle wrapping an <see cref="LspServerInstance"/> so the caller doesn't depend on internals.
/// </summary>
public sealed class LspServerHandle
{
    internal LspServerInstance Instance { get; }
    internal LspServerHandle(LspServerInstance instance) => Instance = instance;
}

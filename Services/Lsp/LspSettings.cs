namespace AnswerCode.Services.Lsp;

public sealed class LspSettings
{
    public const string SectionName = "LspServers";

    /// <summary>
    /// Per-language LSP server configurations. Key = language id (e.g. "typescript", "python").
    /// </summary>
    public Dictionary<string, LspServerConfig> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Seconds to wait for a single LSP request before falling back to regex.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Seconds an idle server lives before being shut down.
    /// </summary>
    public int IdleTimeoutSeconds { get; set; } = 300;
}

public sealed class LspServerConfig
{
    /// <summary>
    /// The command to execute (e.g. "node").
    /// </summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Arguments to pass to the command.
    /// </summary>
    public string[] Arguments { get; set; } = [];

    /// <summary>
    /// Language IDs this server handles (e.g. ["typescript", "typescriptreact", "javascript", "javascriptreact"]).
    /// </summary>
    public string[] LanguageIds { get; set; } = [];
}

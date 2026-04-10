using System.Diagnostics;
using System.Text.Json;

namespace AnswerCode.Services.Lsp;

/// <summary>
/// Wraps a single LSP server process: start → initialize handshake → ready → requests → shutdown.
/// </summary>
internal sealed class LspServerInstance : IAsyncDisposable
{
    private readonly LspServerConfig _config;
    private readonly string _rootPath;
    private readonly ILogger _logger;
    private readonly TimeSpan _requestTimeout;

    private Process? _process;
    private LspJsonRpcClient? _rpc;
    private bool _ready;
    private bool _disposed;

    private DateTime _lastUsed = DateTime.UtcNow;
    internal DateTime LastUsed => _lastUsed;
    internal bool IsReady => _ready && _process is { HasExited: false };

    internal LspServerInstance(LspServerConfig config, string rootPath, TimeSpan requestTimeout, ILogger logger)
    {
        _config = config;
        _rootPath = rootPath;
        _requestTimeout = requestTimeout;
        _logger = logger;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    internal async Task StartAsync(CancellationToken ct)
    {
        var command = ResolveCommand(_config.Command);
        _logger.LogInformation("Starting LSP server: {Cmd} {Args} (rootPath={Root})", command, string.Join(" ", _config.Arguments), _rootPath);

        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = _rootPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in _config.Arguments)
        {
            psi.ArgumentList.Add(ResolveRelativePath(arg));
        }

        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start LSP server: {command}");

        // Log stderr in background
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync(ct) is { } line)
                {
                    _logger.LogDebug("LSP stderr: {Line}", line);
                }
            }
            catch { /* process exited */ }
        }, ct);

        _rpc = new LspJsonRpcClient(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream, _logger);

        // Handle server→client requests (e.g. window/workDoneProgress/create)
        _rpc.OnServerRequest += (id, method, _) =>
        {
            _logger.LogDebug("Handling server request: {Method} (id={Id})", method, id);
            return null; // acknowledge with null result
        };

        // Handle notifications (log for debugging, track progress)
        _rpc.OnNotification += (method, @params) =>
        {
            if (method == "window/logMessage" || method == "$/progress")
            {
                _logger.LogDebug("LSP notification: {Method}", method);
            }
        };

        _rpc.StartReading();

        await InitializeAsync(ct);
        _ready = true;
        _logger.LogInformation("LSP server ready for {Root}", _rootPath);
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var rootUri = new Uri(_rootPath).AbsoluteUri;

        var initParams = new InitializeParams
        {
            ProcessId = Environment.ProcessId,
            RootUri = rootUri,
            Capabilities = new ClientCapabilities
            {
                TextDocument = new TextDocumentClientCapabilities
                {
                    DocumentSymbol = new DocumentSymbolCapability { HierarchicalDocumentSymbolSupport = true },
                    Definition = new DefinitionCapability { LinkSupport = false },
                    References = new ReferencesCapability { DynamicRegistration = false },
                },
                Window = new WindowClientCapabilities { WorkDoneProgress = true },
            },
        };

        var result = await _rpc!.SendRequestAsync("initialize", initParams, TimeSpan.FromSeconds(30), ct);
        _logger.LogDebug("LSP initialize response received, capabilities: {Caps}", result.ValueKind != JsonValueKind.Undefined ? result.GetRawText()[..Math.Min(500, result.GetRawText().Length)] : "(empty)");

        // Send initialized notification
        await _rpc.SendNotificationAsync("initialized", new { }, ct);

        // Give the server a moment to finish post-initialization (e.g. pyright background analysis)
        await Task.Delay(1000, ct);
    }

    // ── LSP requests ─────────────────────────────────────────────────────────

    internal async Task<List<DocumentSymbol>> GetDocumentSymbolsAsync(string fileUri, CancellationToken ct)
    {
        Touch();
        var @params = new DocumentSymbolParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = fileUri },
        };

        var result = await _rpc!.SendRequestAsync("textDocument/documentSymbol", @params, _requestTimeout, ct);

        if (result.ValueKind == JsonValueKind.Undefined || result.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<DocumentSymbol>>(result.GetRawText(), _jsonOptions) ?? [];
    }

    internal async Task<List<LspLocation>> GetDefinitionsAsync(string fileUri, int line, int character, CancellationToken ct)
    {
        Touch();
        var @params = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = fileUri },
            Position = new LspPosition { Line = line, Character = character },
        };

        var result = await _rpc!.SendRequestAsync("textDocument/definition", @params, _requestTimeout, ct);
        if (result.ValueKind == JsonValueKind.Undefined || result.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        // definition can return Location | Location[] | LocationLink[]
        if (result.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<LspLocation>>(result.GetRawText(), _jsonOptions) ?? [];
        }

        var single = JsonSerializer.Deserialize<LspLocation>(result.GetRawText(), _jsonOptions);
        return single is not null ? [single] : [];
    }

    internal async Task<List<LspLocation>> GetReferencesAsync(string fileUri, int line, int character, bool includeDeclaration, CancellationToken ct)
    {
        Touch();
        var @params = new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = fileUri },
            Position = new LspPosition { Line = line, Character = character },
            Context = new ReferenceContext { IncludeDeclaration = includeDeclaration },
        };

        var result = await _rpc!.SendRequestAsync("textDocument/references", @params, _requestTimeout, ct);
        if (result.ValueKind == JsonValueKind.Undefined || result.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<LspLocation>>(result.GetRawText(), _jsonOptions) ?? [];
    }

    internal async Task OpenDocumentAsync(string fileUri, string languageId, string text, CancellationToken ct)
    {
        Touch();
        var @params = new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = fileUri,
                LanguageId = languageId,
                Version = 1,
                Text = text,
            },
        };
        await _rpc!.SendNotificationAsync("textDocument/didOpen", @params, ct);
    }

    internal async Task CloseDocumentAsync(string fileUri, CancellationToken ct)
    {
        var @params = new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = fileUri },
        };
        await _rpc!.SendNotificationAsync("textDocument/didClose", @params, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Touch() => _lastUsed = DateTime.UtcNow;

    private static string ResolveCommand(string command)
    {
        if (command.StartsWith("./") || command.StartsWith(".\\"))
        {
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), command));
        }

        return command;
    }

    /// <summary>
    /// Resolve relative paths in arguments (e.g. "./lsp-servers/...") to absolute paths
    /// based on the application directory, NOT the rootPath (which is the user's project).
    /// </summary>
    private static string ResolveRelativePath(string arg)
    {
        if (arg.StartsWith("./") || arg.StartsWith(".\\"))
        {
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), arg));
        }

        return arg;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Dispose ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_rpc is not null && _process is { HasExited: false })
        {
            try
            {
                await _rpc.SendRequestAsync("shutdown", null, TimeSpan.FromSeconds(5));
                await _rpc.SendNotificationAsync("exit", null);
            }
            catch { /* best effort */ }
        }

        if (_rpc is not null)
        {
            await _rpc.DisposeAsync();
        }

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        _process?.Dispose();
    }
}

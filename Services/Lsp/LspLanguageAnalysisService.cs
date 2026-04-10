using AnswerCode.Services.Analysis;

namespace AnswerCode.Services.Lsp;

/// <summary>
/// Decorator over <see cref="ILanguageHeuristicService"/>.
/// For TypeScript/Python/Go/Rust: delegates to LSP servers for semantic analysis.
/// For other languages or on LSP failure: falls back to the wrapped regex-based service.
/// </summary>
public sealed class LspLanguageAnalysisService(ILanguageHeuristicService fallback,
                                               ILspServerManager serverManager,
                                               IWorkspaceFileService workspaceFileService,
                                               ILogger<LspLanguageAnalysisService> logger) : ILanguageHeuristicService
{
    private static readonly HashSet<string> _lspLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "typescript",
        "typescriptreact",
        "javascript",
        "javascriptreact",
        "python",
        "go",
        "rust",
    };

    // ── FindDefinitionsAsync ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSymbolMatch>> FindDefinitionsAsync(string rootPath,
                                                                             string symbol,
                                                                             string? filePath = null,
                                                                             string? signatureHint = null,
                                                                             CancellationToken cancellationToken = default)
    {
        // If a filePath is given and it's an LSP language, try LSP-based definition lookup
        if (filePath is not null)
        {
            var langId = workspaceFileService.GetLanguageId(filePath);
            if (langId is not null && _lspLanguages.Contains(langId) && serverManager.HasServerFor(langId))
            {
                try
                {
                    var result = await FindDefinitionsViaLspAsync(rootPath, symbol, filePath, langId, signatureHint, cancellationToken);
                    if (result.Count > 0)
                    {
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "LSP FindDefinitions failed for {Symbol} in {File}, falling back to regex", symbol, filePath);
                }
            }
        }

        return await fallback.FindDefinitionsAsync(rootPath, symbol, filePath, signatureHint, cancellationToken);
    }

    private async Task<IReadOnlyList<SourceSymbolMatch>> FindDefinitionsViaLspAsync(string rootPath,
                                                                                    string symbol,
                                                                                    string filePath,
                                                                                    string langId,
                                                                                    string? signatureHint,
                                                                                    CancellationToken ct)
    {
        var handle = await serverManager.GetServerAsync(rootPath, langId, ct);
        if (handle is null)
        {
            return [];
        }

        var server = handle.Instance;
        var normalizedPath = workspaceFileService.NormalizePath(rootPath, filePath);
        var fileUri = new Uri(normalizedPath).AbsoluteUri;

        // Open the document so the server knows about it
        var text = await File.ReadAllTextAsync(normalizedPath, ct);
        await server.OpenDocumentAsync(fileUri, LspLanguageId(langId), text, ct);

        try
        {
            // Find the symbol position in the file
            var position = FindSymbolPosition(text, symbol);
            if (position is null)
            {
                return [];
            }

            var locations = await server.GetDefinitionsAsync(fileUri, position.Value.line, position.Value.character, ct);

            var matches = new List<SourceSymbolMatch>();
            foreach (var loc in locations)
            {
                var defPath = UriToPath(loc.Uri);
                if (defPath is null)
                {
                    continue;
                }

                var relativePath = workspaceFileService.ToRelativePath(rootPath, defPath);
                var startLine = loc.Range.Start.Line + 1; // LSP is 0-based
                var endLine = loc.Range.End.Line + 1;

                // Read the definition line to build a signature
                var defLines = File.Exists(defPath) ? await File.ReadAllLinesAsync(defPath, ct) : [];
                var signature = startLine <= defLines.Length
                    ? AnalysisFormatting.TruncateSingleLine(defLines[startLine - 1])
                    : symbol;

                var defLangId = workspaceFileService.GetLanguageId(defPath) ?? "unknown";

                matches.Add(new SourceSymbolMatch(
                    Name: symbol,
                    FullyQualifiedName: symbol,
                    Kind: "unknown", // LSP definition doesn't tell us the kind directly
                    FilePath: defPath,
                    RelativePath: relativePath,
                    StartLine: startLine,
                    EndLine: endLine,
                    Signature: signature,
                    ContainingSymbol: "",
                    IsTestSymbol: workspaceFileService.IsTestFile(defPath),
                    Language: defLangId));
            }

            if (!string.IsNullOrWhiteSpace(signatureHint) && matches.Count > 1)
            {
                var narrowed = matches.Where(m => m.Signature.Contains(signatureHint, StringComparison.OrdinalIgnoreCase)).ToList();
                if (narrowed.Count > 0)
                {
                    return narrowed;
                }
            }

            return matches;
        }
        finally
        {
            await server.CloseDocumentAsync(fileUri, ct);
        }
    }

    // ── GetDeclaredSymbolsInFileAsync ────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSymbolMatch>> GetDeclaredSymbolsInFileAsync(string rootPath,
                                                                                      string filePath,
                                                                                      CancellationToken cancellationToken = default)
    {
        var normalizedPath = workspaceFileService.NormalizePath(rootPath, filePath);
        var langId = workspaceFileService.GetLanguageId(normalizedPath);

        if (langId is not null && _lspLanguages.Contains(langId) && serverManager.HasServerFor(langId))
        {
            try
            {
                var result = await GetDeclaredSymbolsViaLspAsync(rootPath, normalizedPath, langId, cancellationToken);
                if (result.Count > 0)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LSP GetDeclaredSymbols failed for {File}, falling back to regex", filePath);
            }
        }

        return await fallback.GetDeclaredSymbolsInFileAsync(rootPath, filePath, cancellationToken);
    }

    private async Task<IReadOnlyList<SourceSymbolMatch>> GetDeclaredSymbolsViaLspAsync(string rootPath,
                                                                                       string normalizedPath,
                                                                                       string langId,
                                                                                       CancellationToken ct)
    {
        var handle = await serverManager.GetServerAsync(rootPath, langId, ct);
        if (handle is null)
        {
            return [];
        }

        var server = handle.Instance;
        var fileUri = new Uri(normalizedPath).AbsoluteUri;

        var text = await File.ReadAllTextAsync(normalizedPath, ct);
        await server.OpenDocumentAsync(fileUri, LspLanguageId(langId), text, ct);

        try
        {
            // Wait briefly for the server to process the document
            await Task.Delay(500, ct);

            var symbols = await server.GetDocumentSymbolsAsync(fileUri, ct);
            var relativePath = workspaceFileService.ToRelativePath(rootPath, normalizedPath);
            var lines = text.Split('\n');

            return FlattenSymbols(symbols, rootPath, normalizedPath, relativePath, langId, lines, "").ToList();
        }
        finally
        {
            await server.CloseDocumentAsync(fileUri, ct);
        }
    }

    private IEnumerable<SourceSymbolMatch> FlattenSymbols(List<DocumentSymbol> symbols,
                                                          string rootPath,
                                                          string filePath,
                                                          string relativePath,
                                                          string langId,
                                                          string[] lines,
                                                          string containingSymbol)
    {
        foreach (var sym in symbols)
        {
            var startLine = sym.Range.Start.Line + 1;
            var endLine = sym.Range.End.Line + 1;
            var kind = LspSymbolKind.ToKindString(sym.Kind);
            var signature = startLine <= lines.Length
                ? AnalysisFormatting.TruncateSingleLine(lines[startLine - 1])
                : sym.Name;
            var fqn = string.IsNullOrEmpty(containingSymbol) ? sym.Name : $"{containingSymbol}.{sym.Name}";

            yield return new SourceSymbolMatch(Name: sym.Name,
                                               FullyQualifiedName: fqn,
                                               Kind: kind,
                                               FilePath: filePath,
                                               RelativePath: relativePath,
                                               StartLine: startLine,
                                               EndLine: endLine,
                                               Signature: signature,
                                               ContainingSymbol: containingSymbol,
                                               IsTestSymbol: workspaceFileService.IsTestFile(filePath),
                                               Language: langId);

            if (sym.Children is { Count: > 0 })
            {
                foreach (var child in FlattenSymbols(sym.Children, rootPath, filePath, relativePath, langId, lines, fqn))
                {
                    yield return child;
                }
            }
        }
    }

    // ── ReadSymbolAsync ──────────────────────────────────────────────────────

    public Task<SymbolReadResult?> ReadSymbolAsync(SourceSymbolMatch match,
                                                   bool includeBody,
                                                   bool includeComments,
                                                   CancellationToken cancellationToken = default)
    {
        // ReadSymbol uses the match's line range (which is already more precise from LSP).
        // The actual file reading logic in the fallback service works fine with LSP-sourced matches.
        return fallback.ReadSymbolAsync(match, includeBody, includeComments, cancellationToken);
    }

    // ── FindReferencesAsync ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<SymbolReferenceMatch>> FindReferencesAsync(string rootPath,
                                                                               SourceSymbolMatch target,
                                                                               string? include = null,
                                                                               string? scope = null,
                                                                               CancellationToken cancellationToken = default)
    {
        var langId = workspaceFileService.GetLanguageId(target.FilePath);

        if (langId is not null && _lspLanguages.Contains(langId) && serverManager.HasServerFor(langId))
        {
            try
            {
                var result = await FindReferencesViaLspAsync(rootPath, target, langId, include, scope, cancellationToken);
                if (result.Count > 0)
                    return result;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LSP FindReferences failed for {Symbol}, falling back to regex", target.Name);
            }
        }

        return await fallback.FindReferencesAsync(rootPath, target, include, scope, cancellationToken);
    }

    private async Task<IReadOnlyList<SymbolReferenceMatch>> FindReferencesViaLspAsync(string rootPath,
                                                                                      SourceSymbolMatch target,
                                                                                      string langId,
                                                                                      string? include,
                                                                                      string? scope,
                                                                                      CancellationToken ct)
    {
        var handle = await serverManager.GetServerAsync(rootPath, langId, ct);
        if (handle is null)
        {
            return [];
        }

        var server = handle.Instance;
        var normalizedPath = workspaceFileService.NormalizePath(rootPath, target.FilePath);
        var fileUri = new Uri(normalizedPath).AbsoluteUri;

        var text = await File.ReadAllTextAsync(normalizedPath, ct);
        await server.OpenDocumentAsync(fileUri, LspLanguageId(langId), text, ct);

        try
        {
            // Use the target's start line + find the symbol position within that line
            var line = target.StartLine - 1; // 0-based
            var character = 0;
            if (line >= 0 && line < text.Split('\n').Length)
            {
                var lineText = text.Split('\n')[line];
                var idx = lineText.IndexOf(target.Name, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    character = idx;
                }
            }

            var locations = await server.GetReferencesAsync(fileUri, line, character, false, ct);

            var results = new List<SymbolReferenceMatch>();
            foreach (var loc in locations)
            {
                var refPath = UriToPath(loc.Uri);
                if (refPath is null)
                {
                    continue;
                }

                var relativePath = workspaceFileService.ToRelativePath(rootPath, refPath);

                if (!AnalysisFormatting.MatchesInclude(include, relativePath))
                {
                    continue;
                }

                var isTestFile = workspaceFileService.IsTestFile(refPath);
                if (!MatchesScope(scope, isTestFile))
                {
                    continue;
                }

                var refLine = loc.Range.Start.Line + 1;
                var lineText = "";

                if (File.Exists(refPath))
                {
                    var refLines = await File.ReadAllLinesAsync(refPath, ct);
                    if (refLine <= refLines.Length)
                    {
                        lineText = AnalysisFormatting.TruncateSingleLine(refLines[refLine - 1]);
                    }
                }

                results.Add(new SymbolReferenceMatch(FilePath: refPath,
                                                     RelativePath: relativePath,
                                                     LineNumber: refLine,
                                                     LineText: lineText,
                                                     Kind: "reference",
                                                     ContainingSymbol: "",
                                                     IsTestFile: isTestFile));
            }

            return results.OrderBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(r => r.LineNumber)
                          .Take(200)
                          .ToList();
        }
        finally
        {
            await server.CloseDocumentAsync(fileUri, ct);
        }
    }

    // ── FindTestsAsync ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<TestFileMatch>> FindTestsAsync(string rootPath,
                                                             IReadOnlyList<SourceSymbolMatch> targets,
                                                             string? testFramework = null,
                                                             CancellationToken cancellationToken = default)
    {
        // LSP has no test discovery protocol — always use regex fallback
        return fallback.FindTestsAsync(rootPath, targets, testFramework, cancellationToken);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (int line, int character)? FindSymbolPosition(string text, string symbol)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].IndexOf(symbol, StringComparison.Ordinal);
            if (idx >= 0)
            {
                return (i, idx);
            }
        }

        return null;
    }

    private static string? UriToPath(string uri)
    {
        try
        {
            var u = new Uri(uri);
            return u.LocalPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps our internal language id to the LSP-standard languageId string.
    /// </summary>
    private static string LspLanguageId(string internalLangId) => internalLangId switch
    {
        "typescriptreact" => "typescriptreact",
        "javascriptreact" => "javascriptreact",
        "javascript" => "javascript",
        "python" => "python",
        "go" => "go",
        "rust" => "rust",
        _ => "typescript", // default for typescript
    };

    private static bool MatchesScope(string? scope, bool isTestFile) => scope?.ToLowerInvariant() switch
    {
        "tests" => isTestFile,
        "production" => !isTestFile,
        _ => true,
    };
}

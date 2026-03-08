using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AnswerCode.Services.Analysis;

public class SymbolAnalysisService(ICSharpCompilationService compilationService,
                                   ILanguageHeuristicService languageHeuristicService,
                                   IWorkspaceFileService workspaceFileService) : ISymbolAnalysisService
{
    public async Task<ResolvedSymbolResult> ResolveSymbolAsync(string rootPath,
                                                               string symbol,
                                                               string? filePath = null,
                                                               string? signatureHint = null,
                                                               CancellationToken cancellationToken = default)
    {
        var candidates = await FindDefinitionsAsync(rootPath, symbol, filePath, signatureHint, cancellationToken);

        if (candidates.Count == 0)
        {
            return new ResolvedSymbolResult(symbol, null, candidates, $"No symbols found for '{symbol}'.");
        }

        if (candidates.Count == 1)
        {
            return new ResolvedSymbolResult(symbol, candidates[0], candidates);
        }

        var narrowed = ApplyDisambiguation(candidates, symbol, filePath, signatureHint);
        if (narrowed.Count == 1)
        {
            return new ResolvedSymbolResult(symbol, narrowed[0], candidates);
        }

        return new ResolvedSymbolResult(symbol, null, narrowed.Count > 0 ? narrowed : candidates);
    }

    public async Task<IReadOnlyList<SourceSymbolMatch>> FindDefinitionsAsync(string rootPath,
                                                                             string symbol,
                                                                             string? filePath = null,
                                                                             string? signatureHint = null,
                                                                             CancellationToken cancellationToken = default)
    {
        var normalizedFilePath = string.IsNullOrWhiteSpace(filePath)
            ? null
            : workspaceFileService.NormalizePath(rootPath, filePath);
        var requestedLanguage = normalizedFilePath is null ? null : workspaceFileService.GetLanguageId(normalizedFilePath);
        var matches = new List<SourceSymbolMatch>();

        if (requestedLanguage is null || string.Equals(requestedLanguage, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            var compilationContext = await compilationService.GetCompilationAsync(rootPath, cancellationToken);
            matches.AddRange(EnumerateSourceSymbols(compilationContext)
                .Where(match => string.Equals(match.Name, symbol, StringComparison.OrdinalIgnoreCase))
                .Where(match => normalizedFilePath is null || string.Equals(match.FilePath, normalizedFilePath, StringComparison.OrdinalIgnoreCase)));
        }

        if (requestedLanguage is null || !string.Equals(requestedLanguage, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            var heuristicMatches = await languageHeuristicService.FindDefinitionsAsync(rootPath, symbol, normalizedFilePath, signatureHint, cancellationToken);
            matches.AddRange(heuristicMatches);
        }

        if (!string.IsNullOrWhiteSpace(signatureHint))
        {
            var hinted = matches
                .Where(match => MatchesSignatureHint(match, signatureHint))
                .ToList();

            if (hinted.Count > 0)
            {
                matches = hinted;
            }
        }

        return matches.OrderByDescending(match => string.Equals(match.Name, symbol, StringComparison.Ordinal))
                      .ThenByDescending(match => MatchesSignatureHint(match, signatureHint))
                      .ThenBy(match => match.RelativePath, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(match => match.StartLine)
                      .ToList();
    }

    public async Task<IReadOnlyList<SourceSymbolMatch>> GetDeclaredSymbolsInFileAsync(string rootPath,
                                                                                      string filePath,
                                                                                      CancellationToken cancellationToken = default)
    {
        var normalizedFilePath = workspaceFileService.NormalizePath(rootPath, filePath);
        var languageId = workspaceFileService.GetLanguageId(normalizedFilePath);

        if (!string.Equals(languageId, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            return await languageHeuristicService.GetDeclaredSymbolsInFileAsync(rootPath, normalizedFilePath, cancellationToken);
        }

        var compilationContext = await compilationService.GetCompilationAsync(rootPath, cancellationToken);

        return EnumerateSourceSymbols(compilationContext).Where(match => string.Equals(match.FilePath, normalizedFilePath, StringComparison.OrdinalIgnoreCase))
                                                         .OrderBy(match => match.StartLine)
                                                         .ToList();
    }

    public async Task<SymbolReadResult?> ReadSymbolAsync(string rootPath,
                                                         string symbol,
                                                         string? filePath,
                                                         bool includeBody,
                                                         bool includeComments,
                                                         string? signatureHint = null,
                                                         CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveSymbolAsync(rootPath, symbol, filePath, signatureHint, cancellationToken);
        if (!resolved.IsSuccess || resolved.Match is null)
        {
            return null;
        }

        if (!string.Equals(resolved.Match.Language, "csharp", StringComparison.OrdinalIgnoreCase)
            || resolved.Match.Symbol is null)
        {
            return await languageHeuristicService.ReadSymbolAsync(resolved.Match, includeBody, includeComments, cancellationToken);
        }

        var syntaxNode = await GetSymbolSyntaxNodeAsync(resolved.Match.Symbol, cancellationToken);
        if (syntaxNode is null)
        {
            return null;
        }

        var syntaxTree = syntaxNode.SyntaxTree;
        var text = await syntaxTree.GetTextAsync(cancellationToken);
        var declarationStartLine = syntaxNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var declarationEndLine = GetDeclarationEndLine(syntaxNode, text);
        var contentStartLine = includeComments ? GetContentStartLine(syntaxNode, text) : declarationStartLine;
        var contentEndLine = includeBody
            ? syntaxNode.GetLastToken(includeZeroWidth: true).GetLocation().GetLineSpan().EndLinePosition.Line + 1
            : declarationEndLine;

        return new SymbolReadResult(resolved.Match,
                                    contentStartLine,
                                    contentEndLine,
                                    declarationStartLine,
                                    declarationEndLine,
                                    includeBody,
                                    includeComments,
                                    AnalysisFormatting.FormatSnippet(text, contentStartLine, contentEndLine));
    }

    private static List<SourceSymbolMatch> ApplyDisambiguation(IReadOnlyList<SourceSymbolMatch> candidates,
                                                               string symbol,
                                                               string? filePath,
                                                               string? signatureHint)
    {
        IEnumerable<SourceSymbolMatch> query = candidates;

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var fileName = Path.GetFileName(filePath);
            var fileMatches = query
                .Where(match => string.Equals(Path.GetFileName(match.FilePath), fileName, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(match.RelativePath, filePath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (fileMatches.Count > 0)
            {
                query = fileMatches;
            }
        }

        if (!string.IsNullOrWhiteSpace(signatureHint))
        {
            var hinted = query.Where(match => MatchesSignatureHint(match, signatureHint)).ToList();
            if (hinted.Count > 0)
            {
                query = hinted;
            }
        }

        var exactCase = query.Where(match => string.Equals(match.Name, symbol, StringComparison.Ordinal)).ToList();
        if (exactCase.Count > 0)
        {
            query = exactCase;
        }

        return [.. query];
    }

    private IEnumerable<SourceSymbolMatch> EnumerateSourceSymbols(CSharpCompilationContext compilationContext)
    {
        var results = new List<SourceSymbolMatch>();
        CollectNamespaceSymbols(compilationContext.Compilation.Assembly.GlobalNamespace, compilationContext, results);
        return results;
    }

    private void CollectNamespaceSymbols(INamespaceSymbol namespaceSymbol, CSharpCompilationContext compilationContext, List<SourceSymbolMatch> results)
    {
        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            CollectNamespaceSymbols(nestedNamespace, compilationContext, results);
        }

        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            CollectTypeSymbols(type, compilationContext, results);
        }
    }

    private void CollectTypeSymbols(INamedTypeSymbol typeSymbol, CSharpCompilationContext compilationContext, List<SourceSymbolMatch> results)
    {
        AddIfSource(typeSymbol, compilationContext, results);

        foreach (var member in typeSymbol.GetMembers())
        {
            switch (member)
            {
                case INamedTypeSymbol nestedType:
                    CollectTypeSymbols(nestedType, compilationContext, results);
                    break;
                case IMethodSymbol methodSymbol when methodSymbol.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor:
                    AddIfSource(methodSymbol, compilationContext, results);
                    break;
                case IPropertySymbol:
                case IFieldSymbol:
                case IEventSymbol:
                    AddIfSource(member, compilationContext, results);
                    break;
            }
        }
    }

    private void AddIfSource(ISymbol symbol, CSharpCompilationContext compilationContext, List<SourceSymbolMatch> results)
    {
        var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource && loc.SourceTree is not null);
        if (location?.SourceTree is null)
        {
            return;
        }

        if (!compilationContext.TreeToPath.TryGetValue(location.SourceTree, out var filePath))
        {
            return;
        }

        if (Path.GetFileName(filePath).EndsWith("ImplicitGlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var lineSpan = location.GetLineSpan();
        var startLine = lineSpan.StartLinePosition.Line + 1;
        var endLine = lineSpan.EndLinePosition.Line + 1;
        var relativePath = workspaceFileService.ToRelativePath(compilationContext.RootPath, filePath);
        var containingSymbol = symbol.ContainingType is not null
            ? symbol.ContainingType.ToDisplayString(AnalysisFormatting.ContainingSymbolFormat)
            : symbol.ContainingNamespace is { IsGlobalNamespace: false }
                ? symbol.ContainingNamespace.ToDisplayString()
                : "(global)";

        results.Add(new SourceSymbolMatch(symbol.Name,
                                          symbol.ToDisplayString(AnalysisFormatting.SignatureDisplayFormat),
                                          GetKindLabel(symbol),
                                          filePath,
                                          relativePath,
                                          startLine,
                                          endLine,
                                          symbol.ToDisplayString(AnalysisFormatting.SignatureDisplayFormat),
                                          containingSymbol,
                                          workspaceFileService.IsTestFile(filePath),
                                          symbol,
                                          "csharp",
                                          symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
    }

    private static bool MatchesSignatureHint(SourceSymbolMatch match, string? signatureHint)
    {
        if (string.IsNullOrWhiteSpace(signatureHint))
        {
            return false;
        }

        return match.Signature.Contains(signatureHint, StringComparison.OrdinalIgnoreCase)
               || match.FullyQualifiedName.Contains(signatureHint, StringComparison.OrdinalIgnoreCase)
               || match.ContainingSymbol.Contains(signatureHint, StringComparison.OrdinalIgnoreCase)
               || match.RelativePath.Contains(signatureHint, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SyntaxNode?> GetSymbolSyntaxNodeAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        var syntaxReference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxReference is null)
        {
            return null;
        }

        var syntaxNode = await syntaxReference.GetSyntaxAsync(cancellationToken);
        return syntaxNode switch
        {
            VariableDeclaratorSyntax variableDeclarator when variableDeclarator.Parent?.Parent is BaseFieldDeclarationSyntax fieldDeclaration => fieldDeclaration,
            _ => syntaxNode
        };
    }

    private static int GetDeclarationEndLine(SyntaxNode syntaxNode, SourceText text)
    {
        var token = syntaxNode switch
        {
            BaseMethodDeclarationSyntax methodDeclaration when methodDeclaration.ExpressionBody is not null => methodDeclaration.SemicolonToken,
            BaseMethodDeclarationSyntax methodDeclaration when methodDeclaration.Body is not null => methodDeclaration.Body.OpenBraceToken,
            BaseMethodDeclarationSyntax methodDeclaration => methodDeclaration.SemicolonToken,
            BaseTypeDeclarationSyntax typeDeclaration when typeDeclaration.OpenBraceToken != default => typeDeclaration.OpenBraceToken,
            BaseTypeDeclarationSyntax typeDeclaration when typeDeclaration.SemicolonToken != default => typeDeclaration.SemicolonToken,
            DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.SemicolonToken,
            PropertyDeclarationSyntax propertyDeclaration when propertyDeclaration.ExpressionBody is not null => propertyDeclaration.SemicolonToken,
            PropertyDeclarationSyntax propertyDeclaration when propertyDeclaration.AccessorList is not null => propertyDeclaration.AccessorList.OpenBraceToken,
            PropertyDeclarationSyntax propertyDeclaration => propertyDeclaration.SemicolonToken,
            EventDeclarationSyntax eventDeclaration when eventDeclaration.AccessorList is not null => eventDeclaration.AccessorList.OpenBraceToken,
            EventDeclarationSyntax eventDeclaration => eventDeclaration.SemicolonToken,
            EventFieldDeclarationSyntax eventFieldDeclaration => eventFieldDeclaration.SemicolonToken,
            BaseFieldDeclarationSyntax fieldDeclaration => fieldDeclaration.SemicolonToken,
            _ => syntaxNode.GetLastToken(includeZeroWidth: true)
        };

        var endLine = token.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
        return Math.Min(text.Lines.Count, Math.Max(1, endLine));
    }

    private static int GetContentStartLine(SyntaxNode syntaxNode, SourceText text)
    {
        var declarationStartLine = syntaxNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var startLine = declarationStartLine;

        for (var lineIndex = declarationStartLine - 2; lineIndex >= 0; lineIndex--)
        {
            var lineText = text.Lines[lineIndex].ToString();
            var trimmed = lineText.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (startLine == declarationStartLine)
                {
                    continue;
                }

                break;
            }

            if (trimmed.StartsWith("///", StringComparison.Ordinal)
                || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("*/", StringComparison.Ordinal)
                || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                startLine = lineIndex + 1;
                continue;
            }

            break;
        }

        return Math.Min(startLine, declarationStartLine);
    }

    private static string GetKindLabel(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                TypeKind.Class => "class",
                TypeKind.Interface => "interface",
                TypeKind.Enum => "enum",
                TypeKind.Struct => "struct",
                TypeKind.Delegate => "delegate",
                _ => "type"
            },
            IMethodSymbol method when method.MethodKind == MethodKind.Constructor => "constructor",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            _ => symbol.Kind.ToString().ToLowerInvariant()
        };
    }
}

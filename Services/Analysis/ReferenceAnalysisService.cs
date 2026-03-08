using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AnswerCode.Services.Analysis;

public class ReferenceAnalysisService(
    ICSharpCompilationService compilationService,
    ILanguageHeuristicService languageHeuristicService,
    ISymbolAnalysisService symbolAnalysisService,
    IWorkspaceFileService workspaceFileService) : IReferenceAnalysisService
{
    private const int _maxReferences = 200;

    public async Task<ReferenceSearchResult> FindReferencesAsync(string rootPath,
                                                                 string symbol,
                                                                 string? filePath = null,
                                                                 string? include = null,
                                                                 string? scope = null,
                                                                 string? signatureHint = null,
                                                                 CancellationToken cancellationToken = default)
    {
        var resolved = await symbolAnalysisService.ResolveSymbolAsync(rootPath, symbol, filePath, signatureHint, cancellationToken);
        if (!resolved.IsSuccess || resolved.Match is null)
        {
            return new ReferenceSearchResult(symbol, null, resolved.Candidates, [], resolved.Message);
        }

        if (!string.Equals(resolved.Match.Language, "csharp", StringComparison.OrdinalIgnoreCase)
            || resolved.Match.Symbol is null)
        {
            var heuristicReferences = await languageHeuristicService.FindReferencesAsync(rootPath, resolved.Match, include, scope, cancellationToken);
            return new ReferenceSearchResult(symbol, resolved.Match, resolved.Candidates, heuristicReferences);
        }

        var compilationContext = await compilationService.GetCompilationAsync(rootPath, cancellationToken);
        var references = new List<SymbolReferenceMatch>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var target = resolved.Match.Symbol!;
        var candidateNames = GetCandidateNames(target);

        foreach (var syntaxTree in compilationContext.Compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!compilationContext.TreeToPath.TryGetValue(syntaxTree, out var sourceFile)
                || Path.GetFileName(sourceFile).EndsWith("ImplicitGlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = workspaceFileService.ToRelativePath(rootPath, sourceFile);
            if (!AnalysisFormatting.MatchesInclude(include, relativePath))
            {
                continue;
            }

            var isTestFile = workspaceFileService.IsTestFile(sourceFile);
            if (!MatchesScope(scope, isTestFile))
            {
                continue;
            }

            var root = await syntaxTree.GetRootAsync(cancellationToken);
            var text = await syntaxTree.GetTextAsync(cancellationToken);
            var semanticModel = compilationContext.Compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);

            foreach (var simpleName in root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (!candidateNames.Contains(simpleName.Identifier.ValueText))
                {
                    continue;
                }

                var symbolInfo = semanticModel.GetSymbolInfo(simpleName, cancellationToken);
                var matchedSymbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                if (!MatchesTarget(matchedSymbol, target))
                {
                    continue;
                }

                var lineNumber = simpleName.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var lineText = AnalysisFormatting.TruncateSingleLine(text.Lines[lineNumber - 1].ToString());
                var containingSymbol = semanticModel.GetEnclosingSymbol(simpleName.SpanStart, cancellationToken)?
                    .ToDisplayString(AnalysisFormatting.SignatureDisplayFormat) ?? "(global)";
                var kind = ClassifyReference(simpleName, target);
                var key = $"{relativePath}|{lineNumber}|{kind}|{simpleName.SpanStart}";

                if (!seen.Add(key))
                {
                    continue;
                }

                references.Add(new SymbolReferenceMatch(sourceFile,
                                                        relativePath,
                                                        lineNumber,
                                                        lineText,
                                                        kind,
                                                        containingSymbol,
                                                        isTestFile));

                if (references.Count >= _maxReferences)
                {
                    break;
                }
            }

            if (references.Count >= _maxReferences)
            {
                break;
            }
        }

        var ordered = references.OrderBy(match => match.RelativePath, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(match => match.LineNumber)
                                .ToList();

        return new ReferenceSearchResult(symbol, resolved.Match, resolved.Candidates, ordered);
    }

    private static HashSet<string> GetCandidateNames(ISymbol symbol)
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            symbol.Name
        };

        if (symbol is INamedTypeSymbol namedType && namedType.Name.EndsWith("Attribute", StringComparison.Ordinal))
        {
            names.Add(namedType.Name[..^"Attribute".Length]);
        }

        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
        {
            names.Add(ctor.ContainingType.Name);
        }

        return names;
    }

    private static bool MatchesScope(string? scope, bool isTestFile)
    {
        return scope?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" => true,
            "tests" or "test" => isTestFile,
            "production" or "prod" or "source" => !isTestFile,
            _ => true
        };
    }

    private static bool MatchesTarget(ISymbol? candidate, ISymbol target)
    {
        if (candidate is null)
        {
            return false;
        }

        if (candidate is IAliasSymbol alias)
        {
            return MatchesTarget(alias.Target, target);
        }

        if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, target.OriginalDefinition))
        {
            return true;
        }

        if (candidate is IMethodSymbol candidateMethod
            && target is IMethodSymbol targetMethod
            && SymbolEqualityComparer.Default.Equals(candidateMethod.ReducedFrom?.OriginalDefinition, targetMethod.OriginalDefinition))
        {
            return true;
        }

        if (candidate is IMethodSymbol constructorCandidate
            && constructorCandidate.MethodKind == MethodKind.Constructor
            && target is INamedTypeSymbol namedTypeTarget
            && SymbolEqualityComparer.Default.Equals(constructorCandidate.ContainingType.OriginalDefinition, namedTypeTarget.OriginalDefinition))
        {
            return true;
        }

        if (candidate is INamedTypeSymbol namedTypeCandidate
            && target is IMethodSymbol constructorTarget
            && constructorTarget.MethodKind == MethodKind.Constructor
            && SymbolEqualityComparer.Default.Equals(namedTypeCandidate.OriginalDefinition, constructorTarget.ContainingType.OriginalDefinition))
        {
            return true;
        }

        return false;
    }

    private static string ClassifyReference(SimpleNameSyntax simpleName, ISymbol target)
    {
        if (simpleName.AncestorsAndSelf().OfType<AttributeSyntax>().Any())
        {
            return "attribute";
        }

        if (simpleName.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().Any() && target is IMethodSymbol)
        {
            return "call";
        }

        if (simpleName.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>().Any())
        {
            return target is INamedTypeSymbol ? "construction" : "call";
        }

        if (simpleName.AncestorsAndSelf().OfType<SimpleBaseTypeSyntax>().Any())
        {
            return target is INamedTypeSymbol { TypeKind: TypeKind.Interface } ? "implementation" : "inheritance";
        }

        if (target is INamedTypeSymbol
            && simpleName.AncestorsAndSelf().OfType<ParameterSyntax>().FirstOrDefault() is not null
            && simpleName.AncestorsAndSelf().OfType<ConstructorDeclarationSyntax>().Any())
        {
            return "constructor injection";
        }

        if (simpleName.AncestorsAndSelf().OfType<UsingDirectiveSyntax>().Any())
        {
            return "import";
        }

        if (simpleName.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().Any()
            || simpleName.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().Any()
            || simpleName.AncestorsAndSelf().OfType<VariableDeclarationSyntax>().Any()
            || simpleName.AncestorsAndSelf().OfType<ParameterSyntax>().Any())
        {
            return "type reference";
        }

        return "reference";
    }
}

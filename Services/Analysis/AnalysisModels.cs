using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace AnswerCode.Services.Analysis;

public sealed record CSharpCompilationContext(string RootPath,
                                              CSharpCompilation Compilation,
                                              IReadOnlyList<string> SourceFiles,
                                              IReadOnlyDictionary<string, SyntaxTree> PathToTree,
                                              IReadOnlyDictionary<SyntaxTree, string> TreeToPath,
                                              string CacheSignature);

public sealed record SourceSymbolMatch(string Name,
                                       string FullyQualifiedName,
                                       string Kind,
                                       string FilePath,
                                       string RelativePath,
                                       int StartLine,
                                       int EndLine,
                                       string Signature,
                                       string ContainingSymbol,
                                       bool IsTestSymbol,
                                       ISymbol? Symbol = null,
                                       string Language = "unknown",
                                       string SymbolKey = "");

public sealed record ResolvedSymbolResult(string Query,
                                          SourceSymbolMatch? Match,
                                          IReadOnlyList<SourceSymbolMatch> Candidates,
                                          string? Message = null)
{
    public bool IsSuccess => Match is not null;
    public bool IsAmbiguous => Match is null && Candidates.Count > 1 && string.IsNullOrWhiteSpace(Message);
}

public sealed record SymbolReadResult(SourceSymbolMatch Match,
                                      int ContentStartLine,
                                      int ContentEndLine,
                                      int DeclarationStartLine,
                                      int DeclarationEndLine,
                                      bool IncludedBody,
                                      bool IncludedComments,
                                      string Content);

public sealed record SymbolReferenceMatch(string FilePath,
                                          string RelativePath,
                                          int LineNumber,
                                          string LineText,
                                          string Kind,
                                          string ContainingSymbol,
                                          bool IsTestFile);

public sealed record ReferenceSearchResult(string Query,
                                           SourceSymbolMatch? Target,
                                           IReadOnlyList<SourceSymbolMatch> Candidates,
                                           IReadOnlyList<SymbolReferenceMatch> References,
                                           string? Message = null)
{
    public bool IsSuccess => Target is not null;
    public bool IsAmbiguous => Target is null && Candidates.Count > 1 && string.IsNullOrWhiteSpace(Message);
}

public sealed record TestCaseMatch(string Name,
                                   int LineNumber,
                                   string Confidence,
                                   int Score,
                                   IReadOnlyList<string> Reasons);

public sealed record TestFileMatch(string FilePath,
                                   string RelativePath,
                                   string Framework,
                                   string Confidence,
                                   int Score,
                                   IReadOnlyList<string> Reasons,
                                   IReadOnlyList<TestCaseMatch> TestCases);

public sealed record TestDiscoveryResult(string Query,
                                         IReadOnlyList<SourceSymbolMatch> Targets,
                                         IReadOnlyList<SourceSymbolMatch> Candidates,
                                         IReadOnlyList<TestFileMatch> Matches,
                                         string? Message = null)
{
    public bool HasTargets => Targets.Count > 0;
    public bool IsAmbiguous => Targets.Count == 0 && Candidates.Count > 1 && string.IsNullOrWhiteSpace(Message);
}

// ── Call Graph Models ─────────────────────────────────────────────────────

public sealed record CallGraphNode(string Name,
                                   string FullyQualifiedName,
                                   string Kind,
                                   string FilePath,
                                   string RelativePath,
                                   int Line,
                                   string Signature);

public sealed record CallGraphEdge(CallGraphNode Source,
                                   CallGraphNode Target,
                                   string Label,
                                   int SourceCallLine);

public sealed record CallGraphResult(string Query,
                                     string Direction,
                                     int Depth,
                                     CallGraphNode? Root,
                                     IReadOnlyList<CallGraphEdge> Edges,
                                     IReadOnlyList<string> Warnings,
                                     string? Message = null)
{
    public bool IsSuccess => Root is not null;
}

internal static class AnalysisFormatting
{
    internal static readonly SymbolDisplayFormat SignatureDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
                       | SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeDefaultValue
                          | SymbolDisplayParameterOptions.IncludeOptionalBrackets,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    internal static readonly SymbolDisplayFormat ContainingSymbolFormat = new(globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                                                                              typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                                                                              genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                                                                              miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    internal static string FormatSnippet(SourceText text, int startLine, int endLine)
    {
        var safeStart = Math.Max(1, startLine);
        var safeEnd = Math.Min(text.Lines.Count, Math.Max(safeStart, endLine));
        var sb = new StringBuilder();

        for (int line = safeStart; line <= safeEnd; line++)
        {
            sb.AppendLine($"{line.ToString().PadLeft(5)}| {text.Lines[line - 1].ToString()}");
        }

        return sb.ToString().TrimEnd();
    }

    internal static string TruncateSingleLine(string text, int maxLength = 180)
    {
        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    internal static string ConfidenceFromScore(int score) => score switch
    {
        >= 80 => "high",
        >= 45 => "medium",
        _ => "low"
    };

    internal static bool MatchesInclude(string? includePattern, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(includePattern))
        {
            return true;
        }

        var normalizedPath = relativePath.Replace('\\', '/');
        var normalizedPattern = includePattern.Replace('\\', '/');
        var regexPattern = "^" + Regex.Escape(normalizedPattern)
            .Replace(@"\*\*", "§§DOUBLESTAR§§")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]")
            .Replace("§§DOUBLESTAR§§", ".*") + "$";

        return Regex.IsMatch(normalizedPath, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
               || Regex.IsMatch(Path.GetFileName(normalizedPath), regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

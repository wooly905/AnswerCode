using System.Text;
using System.Text.RegularExpressions;
using AnswerCode.Services.Analysis;

namespace AnswerCode.Services;

/// <summary>
/// Deterministically expands the initial agent context by detecting symbol-like identifiers
/// in the user's question, verifying they exist in the codebase (via <see cref="ISymbolAnalysisService"/>),
/// and pre-fetching their definition, call graph, and references. This lets the tool-calling loop
/// start with evidence already in hand instead of spending iterations discovering it.
///
/// Only symbols that resolve to a real, unambiguous definition are included — unmatched candidates
/// (ordinary English words that happen to look like identifiers) are silently discarded so the
/// pre-fetched context never contains fabricated information.
/// </summary>
public class ContextExpansionService(ISymbolAnalysisService symbolAnalysisService,
                                     ICallGraphService callGraphService,
                                     IReferenceAnalysisService referenceAnalysisService,
                                     ILogger<ContextExpansionService> logger) : IContextExpansionService
{
    private const int _maxCandidates = 3;
    private const int _maxVerifiedSymbols = 2;
    private const int _maxEdgesPerDirection = 10;
    private const int _maxReferencesShown = 8;
    private const int _maxTotalChars = 3000;

    // PascalCase/camelCase identifiers (e.g. AgentService, resolveSymbol) or backtick-quoted names.
    private static readonly Regex _identifierPattern = new(
        @"`([A-Za-z_][A-Za-z0-9_]*)`|\b([A-Za-z_][a-z0-9]*[A-Z][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "API", "URL", "HTTP", "HTTPS", "JSON", "SQL", "ID", "UI", "CRUD", "REST"
    };

    public async Task<string?> BuildSymbolContextAsync(string rootPath, string question, CancellationToken cancellationToken = default)
    {
        try
        {
            List<string> candidates = ExtractCandidates(question);

            if (candidates.Count == 0)
            {
                return null;
            }

            StringBuilder sb = new();
            int verifiedCount = 0;

            foreach (string candidate in candidates)
            {
                if (verifiedCount >= _maxVerifiedSymbols)
                {
                    break;
                }

                var resolved = await symbolAnalysisService.ResolveSymbolAsync(rootPath, candidate, cancellationToken: cancellationToken);
                
                if (!resolved.IsSuccess)
                {
                    continue; // not a real, unambiguous symbol — skip to avoid fabricated context
                }

                verifiedCount++;
                AppendSymbolSection(sb, resolved.Match!, candidate);

                var downstream = await callGraphService.BuildCallGraphAsync(rootPath, candidate, depth: 1, direction: "downstream", cancellationToken: cancellationToken);
                AppendCallGraphSection(sb, downstream, "CALLS");

                var upstream = await callGraphService.BuildCallGraphAsync(rootPath, candidate, depth: 1, direction: "upstream", cancellationToken: cancellationToken);
                AppendCallGraphSection(sb, upstream, "CALLED BY");

                var references = await referenceAnalysisService.FindReferencesAsync(rootPath, candidate, cancellationToken: cancellationToken);
                AppendReferencesSection(sb, references);
            }

            if (verifiedCount == 0)
            {
                return null;
            }

            var text = sb.ToString().TrimEnd();
            
            if (text.Length > _maxTotalChars)
            {
                text = text[.._maxTotalChars] + "\n... (pre-fetched context truncated)";
            }

            logger.LogInformation("Context expansion pre-fetched {Count} verified symbol(s), {Chars} chars", verifiedCount, text.Length);
            return text;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Context expansion failed; continuing without pre-fetched context");
            return null;
        }
    }

    private static List<string> ExtractCandidates(string question)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> candidates = [];

        foreach (Match m in _identifierPattern.Matches(question))
        {
            string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3 || _stopWords.Contains(name))
            {
                continue;
            }

            if (seen.Add(name))
            {
                candidates.Add(name);
            }

            if (candidates.Count >= _maxCandidates)
            {
                break;
            }
        }

        return candidates;
    }

    private static void AppendSymbolSection(StringBuilder sb, SourceSymbolMatch match, string query)
    {
        sb.AppendLine($"### `{query}` — {match.Kind}");
        sb.AppendLine($"Defined at: {match.RelativePath}:{match.StartLine}-{match.EndLine}");
        sb.AppendLine($"Signature: {match.Signature}");
        sb.AppendLine();
    }

    private static void AppendCallGraphSection(StringBuilder sb, CallGraphResult result, string label)
    {
        if (!result.IsSuccess || result.Edges.Count == 0)
        {
            return;
        }

        sb.AppendLine($"{label} (depth 1):");
        
        foreach (var edge in result.Edges.Take(_maxEdgesPerDirection))
        {
            var node = string.Equals(label, "CALLS", StringComparison.OrdinalIgnoreCase) ? edge.Target : edge.Source;
            var labelSuffix = string.IsNullOrEmpty(edge.Label) ? "" : $" [{edge.Label}]";
            sb.AppendLine($"  - {node.Name} ({node.RelativePath}:{node.Line}){labelSuffix}");
        }
        sb.AppendLine();
    }

    private static void AppendReferencesSection(StringBuilder sb, ReferenceSearchResult result)
    {
        if (!result.IsSuccess || result.References.Count == 0)
        {
            return;
        }

        sb.AppendLine($"REFERENCED IN ({result.References.Count} total, showing up to {_maxReferencesShown}):");
        
        foreach (var reference in result.References.Take(_maxReferencesShown))
        {
            sb.AppendLine($"  - {reference.RelativePath}:{reference.LineNumber} [{reference.Kind}]");
        }
        
        if (result.References.Count > _maxReferencesShown)
        {
            sb.AppendLine($"  - ... {result.References.Count - _maxReferencesShown} more");
        }

        sb.AppendLine();
    }
}

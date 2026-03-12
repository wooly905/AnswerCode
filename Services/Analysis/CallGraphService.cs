using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AnswerCode.Services.Analysis;

public class CallGraphService(
    ICSharpCompilationService compilationService,
    ISymbolAnalysisService symbolAnalysisService,
    IReferenceAnalysisService referenceAnalysisService,
    ILanguageHeuristicService languageHeuristicService,
    IWorkspaceFileService workspaceFileService) : ICallGraphService
{
    private const int _maxEdges = 200;
    private const int _maxDepth = 5;

    // ── Language-specific call-site extraction regexes ────────────────────

    // JS/TS: identifier( or object.method( or new Class( or await func(
    private static readonly Regex _jsTsCallRegex = new(
        @"(?:new\s+)?(?:await\s+)?(?:([A-Za-z_$][\w$]*)\.)*([A-Za-z_$][\w$]*)\s*\(",
        RegexOptions.Compiled);

    // Python: identifier( or self.method( or module.func( or ClassName(
    private static readonly Regex _pythonCallRegex = new(
        @"(?:([A-Za-z_][\w]*)\.)?([A-Za-z_][\w]*)\s*\(",
        RegexOptions.Compiled);

    // Java: methodName( or object.method( or ClassName.staticMethod( or new ClassName(
    private static readonly Regex _javaCallRegex = new(
        @"(?:new\s+)?(?:([A-Za-z_][\w]*)\.)*([A-Za-z_][\w]*)\s*\(",
        RegexOptions.Compiled);

    // Go: functionName( or pkg.Function( or receiver.Method(
    private static readonly Regex _goCallRegex = new(
        @"(?:go\s+|defer\s+)?(?:([A-Za-z_][\w]*)\.)?([A-Za-z_][\w]*)\s*\(",
        RegexOptions.Compiled);

    // Rust: function_name( or Type::method( or self.method( or module::func(
    private static readonly Regex _rustCallRegex = new(
        @"(?:([A-Za-z_][\w]*)(?:::|\.))?\s*([A-Za-z_][\w]*)\s*\(",
        RegexOptions.Compiled);

    // C/C++: function( or obj.method( or obj->method( or Class::method( or new Class(
    private static readonly Regex _cCallRegex = new(
        @"(?:new\s+)?(?:([A-Za-z_][\w]*)(?:::|\.|->))?([A-Za-z_][\w]*)\s*\(",
        RegexOptions.Compiled);

    // Names to skip across all languages (control flow keywords, common built-ins)
    private static readonly HashSet<string> _globalSkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "while", "switch", "catch", "foreach", "return", "throw",
        "sizeof", "typeof", "nameof", "default", "else", "try", "finally",
        "using", "lock", "fixed", "checked", "unchecked", "do", "case",
        // common built-ins
        "print", "println", "printf", "sprintf", "fprintf",
    };

    // Language-specific names to skip
    private static readonly Dictionary<string, HashSet<string>> _languageSkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["javascript"] = new(StringComparer.Ordinal)
        {
            "console", "require", "parseInt", "parseFloat", "setTimeout", "setInterval",
            "clearTimeout", "clearInterval", "JSON", "Math", "Object", "Array",
            "String", "Number", "Boolean", "Promise", "Date", "RegExp", "Map", "Set",
            "Error", "TypeError", "RangeError", "Symbol", "WeakMap", "WeakSet",
            "encodeURIComponent", "decodeURIComponent", "encodeURI", "decodeURI",
            "isNaN", "isFinite", "alert", "confirm", "prompt",
        },
        ["typescript"] = new(StringComparer.Ordinal)
        {
            "console", "require", "parseInt", "parseFloat", "setTimeout", "setInterval",
            "clearTimeout", "clearInterval", "JSON", "Math", "Object", "Array",
            "String", "Number", "Boolean", "Promise", "Date", "RegExp", "Map", "Set",
            "Error", "TypeError", "RangeError", "Symbol", "WeakMap", "WeakSet",
            "encodeURIComponent", "decodeURIComponent", "encodeURI", "decodeURI",
            "isNaN", "isFinite",
        },
        ["python"] = new(StringComparer.Ordinal)
        {
            "print", "len", "range", "int", "str", "float", "bool", "list", "dict",
            "tuple", "set", "type", "isinstance", "issubclass", "hasattr", "getattr",
            "setattr", "delattr", "super", "property", "staticmethod", "classmethod",
            "enumerate", "zip", "map", "filter", "sorted", "reversed", "any", "all",
            "min", "max", "sum", "abs", "round", "open", "input", "repr", "hash",
            "id", "dir", "vars", "globals", "locals", "callable", "iter", "next",
            "format", "chr", "ord", "hex", "oct", "bin", "bytearray", "bytes",
            "memoryview", "frozenset", "complex", "divmod", "pow", "eval", "exec",
            "compile", "breakpoint", "exit", "quit",
        },
        ["java"] = new(StringComparer.Ordinal)
        {
            "System", "String", "Integer", "Long", "Double", "Float", "Boolean",
            "Object", "Class", "Math", "Collections", "Arrays", "List", "Map",
            "Set", "Optional", "Stream", "Collectors",
        },
        ["go"] = new(StringComparer.Ordinal)
        {
            "fmt", "log", "os", "io", "strings", "strconv", "errors", "context",
            "sync", "time", "math", "sort", "bytes", "bufio", "regexp", "path",
            "filepath", "encoding", "json", "http", "net", "reflect", "runtime",
            "make", "len", "cap", "append", "copy", "delete", "close", "panic",
            "recover", "new", "println",
        },
        ["rust"] = new(StringComparer.Ordinal)
        {
            "println", "eprintln", "format", "vec", "panic", "assert", "assert_eq",
            "assert_ne", "debug_assert", "todo", "unimplemented", "unreachable",
            "write", "writeln", "include_str", "include_bytes", "env", "cfg",
            "Box", "Vec", "String", "Option", "Result", "Ok", "Err", "Some", "None",
        },
        ["c"] = new(StringComparer.Ordinal)
        {
            "printf", "fprintf", "sprintf", "snprintf", "scanf", "fscanf", "sscanf",
            "malloc", "calloc", "realloc", "free", "memcpy", "memset", "memmove",
            "strcmp", "strncmp", "strcpy", "strncpy", "strlen", "strcat", "strncat",
            "fopen", "fclose", "fread", "fwrite", "fgets", "fputs", "feof", "fflush",
            "exit", "abort", "atexit", "atoi", "atof", "atol", "strtol", "strtod",
            "assert", "perror", "errno",
        },
        ["cpp"] = new(StringComparer.Ordinal)
        {
            "printf", "fprintf", "sprintf", "snprintf", "scanf", "fscanf", "sscanf",
            "malloc", "calloc", "realloc", "free", "memcpy", "memset", "memmove",
            "strcmp", "strncmp", "strcpy", "strncpy", "strlen", "strcat", "strncat",
            "fopen", "fclose", "fread", "fwrite", "fgets", "fputs", "feof", "fflush",
            "exit", "abort", "atexit", "atoi", "atof", "atol", "strtol", "strtod",
            "assert", "perror",
            "std", "cout", "cin", "cerr", "endl",
            "make_shared", "make_unique", "make_pair", "make_tuple",
            "static_cast", "dynamic_cast", "reinterpret_cast", "const_cast",
            "move", "forward", "swap", "begin", "end",
        },
    };

    public async Task<CallGraphResult> BuildCallGraphAsync(string rootPath,
                                                            string symbol,
                                                            string? filePath = null,
                                                            int depth = 2,
                                                            string direction = "downstream",
                                                            CancellationToken cancellationToken = default)
    {
        depth = Math.Clamp(depth, 1, _maxDepth);
        direction = string.Equals(direction, "upstream", StringComparison.OrdinalIgnoreCase) ? "upstream" : "downstream";

        // Step 1: Resolve root symbol
        var resolved = await symbolAnalysisService.ResolveSymbolAsync(rootPath, symbol, filePath, cancellationToken: cancellationToken);

        if (!resolved.IsSuccess || resolved.Match is null)
        {
            if (resolved.IsAmbiguous)
            {
                var candidates = string.Join("\n", resolved.Candidates.Select(c => $"  {c.RelativePath}:{c.StartLine} — {c.Signature}"));
                return new CallGraphResult(symbol, direction, depth, null, [], [],
                    $"Multiple symbol matches for '{symbol}'. Please specify file_path:\n{candidates}");
            }

            return new CallGraphResult(symbol, direction, depth, null, [], [],
                resolved.Message ?? $"No symbols found for '{symbol}'.");
        }

        var rootMatch = resolved.Match;
        var rootNode = ToCallGraphNode(rootMatch);
        var language = rootMatch.Language;

        if (string.Equals(direction, "upstream", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildUpstreamGraphAsync(rootPath, rootNode, rootMatch, depth, language, cancellationToken);
        }

        return await BuildDownstreamGraphAsync(rootPath, rootNode, rootMatch, depth, language, cancellationToken);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DOWNSTREAM — what does this symbol call?
    // ══════════════════════════════════════════════════════════════════════

    private async Task<CallGraphResult> BuildDownstreamGraphAsync(
        string rootPath, CallGraphNode rootNode, SourceSymbolMatch rootMatch,
        int depth, string language, CancellationToken ct)
    {
        var edges = new List<CallGraphEdge>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GetNodeKey(rootNode) };
        var queue = new Queue<(CallGraphNode node, SourceSymbolMatch match, int currentDepth)>();
        queue.Enqueue((rootNode, rootMatch, 0));

        while (queue.Count > 0 && edges.Count < _maxEdges)
        {
            ct.ThrowIfCancellationRequested();
            var (sourceNode, sourceMatch, currentDepth) = queue.Dequeue();

            if (currentDepth >= depth)
            {
                continue;
            }

            var callees = string.Equals(sourceMatch.Language, "csharp", StringComparison.OrdinalIgnoreCase)
                ? await ExtractCSharpDownstreamCallsAsync(rootPath, sourceMatch, ct)
                : await ExtractHeuristicDownstreamCallsAsync(rootPath, sourceMatch, ct);

            foreach (var (calleeNode, label, callLine) in callees)
            {
                if (edges.Count >= _maxEdges)
                {
                    warnings.Add($"Edge limit reached ({_maxEdges}). Graph is truncated.");
                    break;
                }

                var calleeKey = GetNodeKey(calleeNode);
                var edgeLabel = label;

                // Self-recursion check
                if (string.Equals(calleeKey, GetNodeKey(sourceNode), StringComparison.OrdinalIgnoreCase))
                {
                    edgeLabel = "recursive";
                    edges.Add(new CallGraphEdge(sourceNode, calleeNode, edgeLabel, callLine));
                    continue;
                }

                // Cycle detection
                if (visited.Contains(calleeKey))
                {
                    if (string.IsNullOrEmpty(edgeLabel))
                    {
                        edgeLabel = "cycle";
                    }

                    edges.Add(new CallGraphEdge(sourceNode, calleeNode, edgeLabel, callLine));
                    warnings.Add($"Cycle detected: {sourceNode.Name} → {calleeNode.Name}");
                    continue;
                }

                edges.Add(new CallGraphEdge(sourceNode, calleeNode, edgeLabel, callLine));

                // Only continue traversal for resolved nodes (not unresolved/external)
                if (!string.Equals(label, "unresolved", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(label, "external", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(calleeNode.FilePath))
                {
                    visited.Add(calleeKey);

                    // Try to get the SourceSymbolMatch for next-level traversal
                    var calleeMatch = await TryResolveMatchAsync(rootPath, calleeNode, ct);
                    if (calleeMatch is not null)
                    {
                        queue.Enqueue((calleeNode, calleeMatch, currentDepth + 1));
                    }
                }
            }
        }

        // Add summary warnings
        var interfaceCount = edges.Count(e => string.Equals(e.Label, "interface dispatch", StringComparison.OrdinalIgnoreCase));
        if (interfaceCount > 0)
        {
            warnings.Add($"{interfaceCount} edge(s) marked [interface dispatch] — actual target depends on runtime type.");
        }

        var unresolvedCount = edges.Count(e => string.Equals(e.Label, "unresolved", StringComparison.OrdinalIgnoreCase));
        if (unresolvedCount > 0)
        {
            warnings.Add($"{unresolvedCount} edge(s) marked [unresolved] — target could not be statically resolved.");
        }

        return new CallGraphResult(rootMatch.Name, "downstream", depth, rootNode, edges, warnings);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  UPSTREAM — what calls this symbol?
    // ══════════════════════════════════════════════════════════════════════

    private async Task<CallGraphResult> BuildUpstreamGraphAsync(
        string rootPath, CallGraphNode rootNode, SourceSymbolMatch rootMatch,
        int depth, string language, CancellationToken ct)
    {
        var edges = new List<CallGraphEdge>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GetNodeKey(rootNode) };
        var queue = new Queue<(CallGraphNode node, SourceSymbolMatch match, int currentDepth)>();
        queue.Enqueue((rootNode, rootMatch, 0));

        while (queue.Count > 0 && edges.Count < _maxEdges)
        {
            ct.ThrowIfCancellationRequested();
            var (targetNode, targetMatch, currentDepth) = queue.Dequeue();

            if (currentDepth >= depth)
            {
                continue;
            }

            // Use reference analysis to find callers
            var refResult = await referenceAnalysisService.FindReferencesAsync(
                rootPath, targetMatch.Name, targetMatch.FilePath, scope: "all",
                signatureHint: targetMatch.Signature, cancellationToken: ct);

            if (!refResult.IsSuccess)
            {
                continue;
            }

            // Filter to call-site references only
            var callRefs = refResult.References
                .Where(r => string.Equals(r.Kind, "call", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(r.Kind, "construction", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Group by containing symbol to avoid duplicate caller nodes
            var callerGroups = callRefs
                .GroupBy(r => r.ContainingSymbol, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in callerGroups)
            {
                if (edges.Count >= _maxEdges)
                {
                    warnings.Add($"Edge limit reached ({_maxEdges}). Graph is truncated.");
                    break;
                }

                var firstRef = group.First();
                var callerName = ExtractMethodName(group.Key);

                var callerNode = new CallGraphNode(
                    callerName,
                    group.Key,
                    "method",
                    firstRef.FilePath,
                    firstRef.RelativePath,
                    firstRef.LineNumber,
                    group.Key);

                var callerKey = GetNodeKey(callerNode);

                // Self-recursion
                if (string.Equals(callerKey, GetNodeKey(targetNode), StringComparison.OrdinalIgnoreCase))
                {
                    edges.Add(new CallGraphEdge(callerNode, targetNode, "recursive", firstRef.LineNumber));
                    continue;
                }

                // Cycle detection
                if (visited.Contains(callerKey))
                {
                    edges.Add(new CallGraphEdge(callerNode, targetNode, "cycle", firstRef.LineNumber));
                    warnings.Add($"Cycle detected: {callerNode.Name} → {targetNode.Name}");
                    continue;
                }

                edges.Add(new CallGraphEdge(callerNode, targetNode, "", firstRef.LineNumber));
                visited.Add(callerKey);

                // Continue traversal
                var callerMatch = await TryResolveMatchAsync(rootPath, callerNode, ct);
                if (callerMatch is not null)
                {
                    queue.Enqueue((callerNode, callerMatch, currentDepth + 1));
                }
            }
        }

        return new CallGraphResult(rootMatch.Name, "upstream", depth, rootNode, edges, warnings);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  C# Roslyn-based downstream call extraction
    // ══════════════════════════════════════════════════════════════════════

    private async Task<List<(CallGraphNode node, string label, int callLine)>> ExtractCSharpDownstreamCallsAsync(
        string rootPath, SourceSymbolMatch match, CancellationToken ct)
    {
        var results = new List<(CallGraphNode, string, int)>();

        if (match.Symbol is null)
        {
            return results;
        }

        var syntaxRef = match.Symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef is null)
        {
            return results;
        }

        var syntaxNode = await syntaxRef.GetSyntaxAsync(ct);
        var compilationContext = await compilationService.GetCompilationAsync(rootPath, ct);
        var semanticModel = compilationContext.Compilation.GetSemanticModel(syntaxNode.SyntaxTree, ignoreAccessibility: true);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Walk the syntax tree for invocations
        foreach (var node in syntaxNode.DescendantNodes())
        {
            ct.ThrowIfCancellationRequested();
            ISymbol? resolvedSymbol = null;
            int callLine = 0;

            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation, ct);
                    resolvedSymbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                    callLine = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    break;

                case ObjectCreationExpressionSyntax creation:
                    var ctorInfo = semanticModel.GetSymbolInfo(creation, ct);
                    resolvedSymbol = ctorInfo.Symbol ?? ctorInfo.CandidateSymbols.FirstOrDefault();
                    callLine = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    break;

                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    var implicitInfo = semanticModel.GetSymbolInfo(implicitCreation, ct);
                    resolvedSymbol = implicitInfo.Symbol ?? implicitInfo.CandidateSymbols.FirstOrDefault();
                    callLine = implicitCreation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    break;
            }

            if (resolvedSymbol is null)
            {
                continue;
            }

            // Get the original definition (unwrap generics, etc.)
            var originalSymbol = resolvedSymbol.OriginalDefinition;
            var symbolKey = originalSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (!seen.Add(symbolKey))
            {
                continue;
            }

            // Determine label based on symbol characteristics
            var label = DetermineLabel(originalSymbol);

            // Try to find the source location
            var location = originalSymbol.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree is not null);

            if (location?.SourceTree is not null
                && compilationContext.TreeToPath.TryGetValue(location.SourceTree, out var targetFilePath))
            {
                var lineSpan = location.GetLineSpan();
                var relativePath = workspaceFileService.ToRelativePath(rootPath, targetFilePath);
                var targetNode = new CallGraphNode(
                    originalSymbol.Name,
                    symbolKey,
                    GetKindLabel(originalSymbol),
                    targetFilePath,
                    relativePath,
                    lineSpan.StartLinePosition.Line + 1,
                    originalSymbol.ToDisplayString(AnalysisFormatting.SignatureDisplayFormat));

                results.Add((targetNode, label, callLine));
            }
            else
            {
                // External (framework/library) or unresolved
                var externalNode = new CallGraphNode(
                    originalSymbol.Name,
                    symbolKey,
                    GetKindLabel(originalSymbol),
                    "",
                    "",
                    0,
                    originalSymbol.ToDisplayString(AnalysisFormatting.SignatureDisplayFormat));

                results.Add((externalNode, "external", callLine));
            }
        }

        return results;
    }

    private static string DetermineLabel(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            // Check if the method is on an interface
            if (method.ContainingType?.TypeKind == TypeKind.Interface)
            {
                return "interface dispatch";
            }

            // Check if it's abstract
            if (method.IsAbstract)
            {
                return "interface dispatch";
            }

            // Check if it's virtual/override
            if (method.IsVirtual || method.IsOverride)
            {
                return "virtual dispatch";
            }

            // Check if it's a delegate invoke
            if (method.MethodKind == MethodKind.DelegateInvoke)
            {
                return "unresolved";
            }
        }

        return "";
    }

    private static string GetKindLabel(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol m when m.MethodKind == MethodKind.Constructor => "constructor",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            INamedTypeSymbol => "type",
            _ => symbol.Kind.ToString().ToLowerInvariant()
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Heuristic-based downstream call extraction (non-C# languages)
    // ══════════════════════════════════════════════════════════════════════

    private async Task<List<(CallGraphNode node, string label, int callLine)>> ExtractHeuristicDownstreamCallsAsync(
        string rootPath, SourceSymbolMatch match, CancellationToken ct)
    {
        var results = new List<(CallGraphNode, string, int)>();

        // Read the symbol body
        var symbolRead = await languageHeuristicService.ReadSymbolAsync(match, includeBody: true, includeComments: false, ct);
        if (symbolRead is null || string.IsNullOrWhiteSpace(symbolRead.Content))
        {
            return results;
        }

        var language = NormalizeLanguageId(match.Language);
        var bodyLines = symbolRead.Content.Split('\n');
        var callRegex = GetCallRegexForLanguage(language);
        if (callRegex is null)
        {
            return results;
        }

        var skipNames = GetSkipNamesForLanguage(language);
        var extractedCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // name -> first line number

        foreach (var line in bodyLines)
        {
            ct.ThrowIfCancellationRequested();

            // Extract line number from the formatted output (e.g. "  123| code here")
            var lineNumber = ExtractLineNumber(line);
            var codeText = ExtractCodeText(line);

            if (string.IsNullOrWhiteSpace(codeText))
            {
                continue;
            }

            // Skip comment lines
            var trimmed = codeText.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith('#') || trimmed.StartsWith("/*")
                || trimmed.StartsWith('*') || trimmed.StartsWith("*/"))
            {
                continue;
            }

            // Skip string literals (simple heuristic: lines that are mostly strings)
            if (IsLikelyStringLiteral(trimmed))
            {
                continue;
            }

            foreach (Match m in callRegex.Matches(codeText))
            {
                var callName = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value;
                var qualifier = m.Groups[1].Success && m.Groups[2].Success ? m.Groups[1].Value : null;

                if (string.IsNullOrWhiteSpace(callName))
                {
                    continue;
                }

                // Skip control keywords and built-ins
                if (_globalSkipNames.Contains(callName) || skipNames.Contains(callName))
                {
                    continue;
                }

                // Skip if qualifier is a known built-in module/object
                if (qualifier is not null && skipNames.Contains(qualifier))
                {
                    continue;
                }

                if (!extractedCalls.ContainsKey(callName))
                {
                    extractedCalls[callName] = lineNumber > 0 ? lineNumber : match.StartLine;
                }
            }
        }

        // Try to resolve each extracted call name
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (callName, callLine) in extractedCalls)
        {
            ct.ThrowIfCancellationRequested();

            if (!seen.Add(callName))
            {
                continue;
            }

            // Self-call: check if calling our own symbol
            if (string.Equals(callName, match.Name, StringComparison.OrdinalIgnoreCase))
            {
                var selfNode = ToCallGraphNode(match);
                results.Add((selfNode, "recursive", callLine));
                continue;
            }

            // Try to find the definition in the project
            var definitions = await symbolAnalysisService.FindDefinitionsAsync(rootPath, callName, cancellationToken: ct);

            if (definitions.Count > 0)
            {
                // Take the best match (prefer same file, then same language)
                var best = definitions
                    .OrderByDescending(d => string.Equals(d.FilePath, match.FilePath, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenByDescending(d => string.Equals(d.Language, match.Language, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .First();

                var targetNode = ToCallGraphNode(best);
                var label = definitions.Count > 1 ? "ambiguous" : "";

                // Check for interface/abstract dispatch
                if (string.Equals(best.Kind, "interface", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(best.Kind, "abstract", StringComparison.OrdinalIgnoreCase))
                {
                    label = "interface dispatch";
                }

                results.Add((targetNode, label, callLine));
            }
            else
            {
                // Could not resolve — mark as unresolved
                var unresolvedNode = new CallGraphNode(
                    callName, callName, "unknown", "", "", 0, callName + "(...)");
                results.Add((unresolvedNode, "unresolved", callLine));
            }

            // Limit per-symbol callees to avoid explosion
            if (results.Count >= 50)
            {
                break;
            }
        }

        return results;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════════

    private async Task<SourceSymbolMatch?> TryResolveMatchAsync(string rootPath, CallGraphNode node, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(node.FilePath))
        {
            return null;
        }

        try
        {
            var resolved = await symbolAnalysisService.ResolveSymbolAsync(
                rootPath, node.Name, node.FilePath, node.Signature, ct);
            return resolved.Match;
        }
        catch
        {
            return null;
        }
    }

    private static CallGraphNode ToCallGraphNode(SourceSymbolMatch match)
    {
        return new CallGraphNode(
            match.Name,
            match.FullyQualifiedName,
            match.Kind,
            match.FilePath,
            match.RelativePath,
            match.StartLine,
            match.Signature);
    }

    private static string GetNodeKey(CallGraphNode node)
    {
        if (!string.IsNullOrEmpty(node.RelativePath))
        {
            return $"{node.RelativePath}:{node.Name}:{node.Line}";
        }

        return node.FullyQualifiedName;
    }

    private static string ExtractMethodName(string fullyQualifiedName)
    {
        // Extract the method name from "Namespace.Class.Method(params)"
        var parenIndex = fullyQualifiedName.IndexOf('(');
        var nameWithoutParams = parenIndex >= 0 ? fullyQualifiedName[..parenIndex] : fullyQualifiedName;
        var lastDot = nameWithoutParams.LastIndexOf('.');
        return lastDot >= 0 ? nameWithoutParams[(lastDot + 1)..] : nameWithoutParams;
    }

    private static Regex? GetCallRegexForLanguage(string language)
    {
        return language switch
        {
            "javascript" or "typescript" => _jsTsCallRegex,
            "python" => _pythonCallRegex,
            "java" => _javaCallRegex,
            "go" => _goCallRegex,
            "rust" => _rustCallRegex,
            "c" or "cpp" => _cCallRegex,
            _ => _jsTsCallRegex // fallback to a general pattern
        };
    }

    private static HashSet<string> GetSkipNamesForLanguage(string language)
    {
        if (_languageSkipNames.TryGetValue(language, out var skipNames))
        {
            return skipNames;
        }

        return [];
    }

    private static string NormalizeLanguageId(string language)
    {
        return language.ToLowerInvariant() switch
        {
            "csharp" or "c#" => "csharp",
            "javascript" or "js" => "javascript",
            "typescript" or "ts" => "typescript",
            "python" or "py" => "python",
            "java" => "java",
            "go" or "golang" => "go",
            "rust" or "rs" => "rust",
            "c" => "c",
            "cpp" or "c++" => "cpp",
            _ => language.ToLowerInvariant()
        };
    }

    private static int ExtractLineNumber(string formattedLine)
    {
        // Format: "  123| code here"
        var pipeIndex = formattedLine.IndexOf('|');
        if (pipeIndex <= 0)
        {
            return 0;
        }

        var numStr = formattedLine[..pipeIndex].Trim();
        return int.TryParse(numStr, out var num) ? num : 0;
    }

    private static string ExtractCodeText(string formattedLine)
    {
        var pipeIndex = formattedLine.IndexOf('|');
        if (pipeIndex >= 0 && pipeIndex < formattedLine.Length - 1)
        {
            return formattedLine[(pipeIndex + 1)..];
        }

        return formattedLine;
    }

    private static bool IsLikelyStringLiteral(string trimmedLine)
    {
        // Simple heuristic: if line starts with a quote and has very little code structure
        if (trimmedLine.StartsWith('"') || trimmedLine.StartsWith('\'') || trimmedLine.StartsWith('`'))
        {
            return true;
        }

        return false;
    }
}

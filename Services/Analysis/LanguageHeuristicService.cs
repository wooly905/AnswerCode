using System.Text.RegularExpressions;

namespace AnswerCode.Services.Analysis;

public class LanguageHeuristicService : ILanguageHeuristicService
{
    private static readonly Regex _jsTsTypeRegex = new(@"^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?(class|interface|type|enum)\s+([A-Za-z_$][\w$]*)", RegexOptions.Compiled);
    private static readonly Regex _jsTsFunctionRegex = new(@"^\s*(?:export\s+)?(?:async\s+)?(?:default\s+)?function\s+([A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex _jsTsVariableFunctionRegex = new(@"^\s*(?:export\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*=>|[A-Za-z_$][\w$]*\s*=>)", RegexOptions.Compiled);
    private static readonly Regex _jsTsMethodRegex = new(@"^\s*(?:(?:public|private|protected|static|async|abstract|readonly|get|set|override)\s+)*(?:#)?([A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);

    private static readonly Regex _pythonClassRegex = new(@"^\s*class\s+([A-Za-z_][\w]*)", RegexOptions.Compiled);
    private static readonly Regex _pythonFunctionRegex = new(@"^\s*(?:async\s+)?def\s+([A-Za-z_][\w]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex _pythonAssignmentRegex = new(@"^\s*([A-Za-z_][\w]*)\s*=", RegexOptions.Compiled);

    private static readonly Regex _javaTypeRegex = new(@"^\s*(?:(?:public|private|protected|static|abstract|final|sealed)\s+)*(class|interface|enum|record|@interface)\s+([A-Za-z_][\w]*)", RegexOptions.Compiled);
    private static readonly Regex _javaMethodRegex = new(@"^\s*(?:(?:public|private|protected|static|abstract|final|synchronized|native|default|override)\s+)*[A-Za-z_][\w<>,\[\]?\s\.]*\s+([A-Za-z_][\w]*)\s*\(", RegexOptions.Compiled);

    private static readonly Regex _goFunctionRegex = new(@"^\s*func\s+(?:\([^)]*\)\s+)?([A-Za-z_][\w]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex _goTypeRegex = new(@"^\s*type\s+([A-Za-z_][\w]*)\b", RegexOptions.Compiled);
    private static readonly Regex _goVarConstRegex = new(@"^\s*(?:var|const)\s+([A-Za-z_][\w]*)\b", RegexOptions.Compiled);

    private static readonly Regex _rustTypeRegex = new(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(struct|enum|trait|type|mod|const|static)\s+([A-Za-z_][\w]*)", RegexOptions.Compiled);
    private static readonly Regex _rustFunctionRegex = new(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?fn\s+([A-Za-z_][\w]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex _rustImplRegex = new(@"^\s*impl(?:<[^>]+>)?\s+(?:[A-Za-z_][\w]*\s+for\s+)?([A-Za-z_][\w]*)", RegexOptions.Compiled);
    private static readonly Regex _cTypeRegex = new(@"^\s*(typedef\s+)?(struct|enum|union)\s+([A-Za-z_][\w]*)", RegexOptions.Compiled);
    private static readonly Regex _cppClassRegex = new(@"^\s*(?:template\s*<[^>]+>\s*)?(class|struct|enum|namespace)\s+([A-Za-z_][\w]*)", RegexOptions.Compiled);
    private static readonly Regex _cFamilyFunctionRegex = new(@"^\s*(?:[A-Za-z_][\w:\*<>\[\]\s,&]+)?\s+([A-Za-z_][\w:]*)\s*\([^;]*\)\s*(?:\{|;)$", RegexOptions.Compiled);

    private static readonly Regex _jsTestCaseRegex = new("\\b(?:it|test|describe)\\s*\\(\\s*['\"`](.+?)['\"`]", RegexOptions.Compiled);
    private static readonly Regex _pythonTestFunctionRegex = new(@"^\s*def\s+(test_[A-Za-z_][\w]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex _javaTestMethodRegex = new(@"^\s*(?:(?:public|private|protected|static)\s+)*[A-Za-z_][\w<>,\[\]?\s\.]*\s+([A-Za-z_][\w]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex _goTestCaseRegex = new(@"^\s*func\s+(Test[A-Za-z_][\w]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex _rustTestCaseRegex = new(@"^\s*fn\s+([A-Za-z_][\w]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex _cppTestCaseRegex = new(@"\b(?:TEST|TEST_F|TEST_P|TYPED_TEST|TEST_CASE)\s*\((.+?)\)", RegexOptions.Compiled);

    private static readonly string[] _controlKeywords =
    [
        "if",
        "for",
        "while",
        "switch",
        "catch",
        "foreach",
        "return",
        "throw",
        "new",
        "sizeof"
    ];

    private readonly IWorkspaceFileService _workspaceFileService;

    public LanguageHeuristicService(IWorkspaceFileService workspaceFileService)
    {
        _workspaceFileService = workspaceFileService;
    }

    public async Task<IReadOnlyList<SourceSymbolMatch>> FindDefinitionsAsync(string rootPath,
                                                                             string symbol,
                                                                             string? filePath = null,
                                                                             string? signatureHint = null,
                                                                             CancellationToken cancellationToken = default)
    {
        var files = ResolveCandidateFiles(rootPath, filePath).Where(path => !string.Equals(_workspaceFileService.GetLanguageId(path), "csharp", StringComparison.OrdinalIgnoreCase)).ToList();

        var matches = new List<SourceSymbolMatch>();
        foreach (var candidateFile in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var declarations = await GetDeclaredSymbolsInFileAsync(rootPath, candidateFile, cancellationToken);
            matches.AddRange(declarations.Where(match => string.Equals(match.Name, symbol, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(signatureHint))
        {
            var narrowed = matches.Where(match => MatchesSignatureHint(match, signatureHint)).ToList();
            if (narrowed.Count > 0)
            {
                matches = narrowed;
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
        var normalizedPath = _workspaceFileService.NormalizePath(rootPath, filePath);
        var languageId = _workspaceFileService.GetLanguageId(normalizedPath);
        if (languageId is null || string.Equals(languageId, "csharp", StringComparison.OrdinalIgnoreCase) || !File.Exists(normalizedPath))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(normalizedPath, cancellationToken);
        var declarations = ParseDeclarations(languageId, lines).Select(declaration => ToSourceSymbolMatch(rootPath, normalizedPath, languageId, declaration)).ToList();

        return declarations;
    }

    public async Task<SymbolReadResult?> ReadSymbolAsync(SourceSymbolMatch match,
                                                         bool includeBody,
                                                         bool includeComments,
                                                         CancellationToken cancellationToken = default)
    {
        if (!File.Exists(match.FilePath))
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(match.FilePath, cancellationToken);
        var declarationStartLine = Math.Max(1, match.StartLine);
        var declarationEndLine = DetermineDeclarationEndLine(lines, declarationStartLine, match.Language);
        var contentStartLine = includeComments ? FindCommentStartLine(lines, declarationStartLine, match.Language) : declarationStartLine;
        var contentEndLine = includeBody
            ? Math.Max(match.EndLine, declarationEndLine)
            : declarationEndLine;

        var content = string.Join(Environment.NewLine,
                                  Enumerable.Range(contentStartLine, Math.Max(0, contentEndLine - contentStartLine + 1)).Where(lineNumber => lineNumber >= 1 && lineNumber <= lines.Length).Select(lineNumber => $"{lineNumber.ToString().PadLeft(5)}| {lines[lineNumber - 1]}"));

        return new SymbolReadResult(match,
                                    contentStartLine,
                                    contentEndLine,
                                    declarationStartLine,
                                    declarationEndLine,
                                    includeBody,
                                    includeComments,
                                    content);
    }

    public async Task<IReadOnlyList<SymbolReferenceMatch>> FindReferencesAsync(string rootPath,
                                                                               SourceSymbolMatch target,
                                                                               string? include = null,
                                                                               string? scope = null,
                                                                               CancellationToken cancellationToken = default)
    {
        var files = _workspaceFileService.EnumerateSupportedSourceFiles(rootPath);
        var regex = new Regex($@"\b{Regex.Escape(target.Name)}\b", RegexOptions.Compiled);
        var results = new List<SymbolReferenceMatch>();
        var declarationCache = new Dictionary<string, IReadOnlyList<SourceSymbolMatch>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = _workspaceFileService.ToRelativePath(rootPath, file);

            if (!AnalysisFormatting.MatchesInclude(include, relativePath))
            {
                continue;
            }

            var isTestFile = _workspaceFileService.IsTestFile(file);

            if (!MatchesScope(scope, isTestFile))
            {
                continue;
            }

            var lines = await File.ReadAllLinesAsync(file, cancellationToken);

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];

                if (!regex.IsMatch(line))
                {
                    continue;
                }

                var lineNumber = index + 1;

                if (string.Equals(file, target.FilePath, StringComparison.OrdinalIgnoreCase)
                    && lineNumber == target.StartLine
                    && LooksLikeDefinition(line, target.Name, _workspaceFileService.GetLanguageId(file)))
                {
                    continue;
                }

                if (!declarationCache.TryGetValue(file, out var declarations))
                {
                    declarations = await GetDeclaredSymbolsInFileAsync(rootPath, file, cancellationToken);
                    declarationCache[file] = declarations;
                }

                var containingSymbol = FindContainingSymbol(declarations, lineNumber);
                results.Add(new SymbolReferenceMatch(file,
                                                     relativePath,
                                                     lineNumber,
                                                     AnalysisFormatting.TruncateSingleLine(line),
                                                     ClassifyReference(line, target),
                                                     containingSymbol,
                                                     isTestFile));
            }
        }

        return results.OrderBy(match => match.RelativePath, StringComparer.OrdinalIgnoreCase)
                      .ThenBy(match => match.LineNumber)
                      .Take(200)
                      .ToList();
    }

    public async Task<IReadOnlyList<TestFileMatch>> FindTestsAsync(string rootPath,
                                                                   IReadOnlyList<SourceSymbolMatch> targets,
                                                                   string? testFramework = null,
                                                                   CancellationToken cancellationToken = default)
    {
        var files = _workspaceFileService.EnumerateSupportedSourceFiles(rootPath);
        var matches = new List<TestFileMatch>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var languageId = _workspaceFileService.GetLanguageId(file);
            if (languageId is null || string.Equals(languageId, "csharp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = await File.ReadAllLinesAsync(file, cancellationToken);
            var testCases = ParseTestCases(languageId, lines);
            if (!_workspaceFileService.IsTestFile(file) && testCases.Count == 0)
            {
                continue;
            }

            var relativePath = _workspaceFileService.ToRelativePath(rootPath, file);
            var fileText = string.Join(Environment.NewLine, lines);
            var fileScore = 0;
            var fileReasons = new List<string>();
            var matchedCases = new List<TestCaseMatch>();

            foreach (var target in targets)
            {
                var ownerName = GetOwnerTypeName(target);
                if (FileNameLooksRelated(file, target.Name, ownerName))
                {
                    fileScore += 20;
                    fileReasons.Add($"file name matches '{ownerName ?? target.Name}'");
                }

                var mentions = CountMentions(fileText, target.Name) + (ownerName is null ? 0 : CountMentions(fileText, ownerName));
                if (mentions > 0)
                {
                    fileScore += Math.Min(30, mentions * 8);
                    fileReasons.Add("direct symbol mentions found in test file");
                }

                foreach (var testCase in testCases)
                {
                    var caseScore = 0;
                    var caseReasons = new List<string>();

                    if (testCase.Name.Contains(target.Name, StringComparison.OrdinalIgnoreCase)
                        || (ownerName is not null && testCase.Name.Contains(ownerName, StringComparison.OrdinalIgnoreCase)))
                    {
                        caseScore += 20;
                        caseReasons.Add($"test case name mentions '{target.Name}'");
                    }

                    var testWindowText = GetWindowText(lines, testCase.LineNumber, 10);
                    if (CountMentions(testWindowText, target.Name) > 0 || (ownerName is not null && CountMentions(testWindowText, ownerName) > 0))
                    {
                        caseScore += 35;
                        caseReasons.Add("test case body references the target symbol");
                    }

                    if (caseScore == 0)
                    {
                        continue;
                    }

                    matchedCases.Add(new TestCaseMatch(testCase.Name,
                                                       testCase.LineNumber,
                                                       AnalysisFormatting.ConfidenceFromScore(caseScore),
                                                       caseScore,
                                                       caseReasons));
                }
            }

            if (fileScore == 0 && matchedCases.Count == 0)
            {
                continue;
            }

            if (matchedCases.Count > 0)
            {
                fileScore += Math.Min(35, matchedCases.Max(testCase => testCase.Score));
                fileReasons.Add("contains matching test cases");
            }

            matches.Add(new TestFileMatch(file,
                                          relativePath,
                                          testFramework ?? GetDefaultTestFramework(languageId),
                                          AnalysisFormatting.ConfidenceFromScore(fileScore),
                                          fileScore,
                                          fileReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                                          matchedCases.OrderByDescending(testCase => testCase.Score).ThenBy(testCase => testCase.LineNumber).Take(10).ToList()));
        }

        return matches.OrderByDescending(match => match.Score)
                      .ThenBy(match => match.RelativePath, StringComparer.OrdinalIgnoreCase)
                      .Take(20)
                      .ToList();
    }

    private IEnumerable<string> ResolveCandidateFiles(string rootPath, string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normalizedPath = _workspaceFileService.NormalizePath(rootPath, filePath);
            if (File.Exists(normalizedPath))
            {
                return [normalizedPath];
            }
        }

        return _workspaceFileService.EnumerateSupportedSourceFiles(rootPath);
    }

    private SourceSymbolMatch ToSourceSymbolMatch(string rootPath,
                                                  string filePath,
                                                  string languageId,
                                                  HeuristicDeclaration declaration)
    {
        var relativePath = _workspaceFileService.ToRelativePath(rootPath, filePath);
        var fullyQualifiedName = declaration.ContainingSymbol == "(global)"
            ? declaration.Name
            : $"{declaration.ContainingSymbol}.{declaration.Name}";
        return new SourceSymbolMatch(declaration.Name,
                                     fullyQualifiedName,
                                     declaration.Kind,
                                     filePath,
                                     relativePath,
                                     declaration.StartLine,
                                     declaration.EndLine,
                                     declaration.Signature,
                                     declaration.ContainingSymbol,
                                     _workspaceFileService.IsTestFile(filePath),
                                     null,
                                     languageId,
                                     $"{languageId}:{relativePath}:{declaration.StartLine}:{declaration.Name}");
    }

    private List<HeuristicDeclaration> ParseDeclarations(string languageId, string[] lines)
    {
        return languageId switch
        {
            "javascript" or "typescript" => ParseJsTsDeclarations(lines),
            "python" => ParsePythonDeclarations(lines),
            "java" => ParseJavaDeclarations(lines),
            "go" => ParseGoDeclarations(lines),
            "rust" => ParseRustDeclarations(lines),
            "c" or "cpp" => ParseCFamilyDeclarations(lines, languageId),
            _ => []
        };
    }

    private List<HeuristicDeclaration> ParseJsTsDeclarations(string[] lines)
    {
        var results = new List<HeuristicDeclaration>();
        var containers = new Stack<(string Name, int Depth)>();
        var braceDepth = 0;
        var inBlockComment = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (HandleBlockComments(trimmed, ref inBlockComment))
            {
                continue;
            }

            var startDepth = braceDepth;
            UpdateBraceDepth(line, ref braceDepth);
            while (containers.Count > 0 && containers.Peek().Depth >= braceDepth)
            {
                containers.Pop();
            }

            if (ShouldSkipLine(trimmed, "javascript"))
            {
                continue;
            }

            var containingSymbol = containers.Count > 0 ? containers.Peek().Name : "(global)";
            if (TryCreateDeclaration(_jsTsTypeRegex, trimmed, index, lines, 2, 1, containingSymbol, "type", "javascript", out var typeDeclaration))
            {
                results.Add(typeDeclaration!);
                containers.Push((typeDeclaration!.Name, Math.Max(startDepth, 0)));
                continue;
            }

            if (TryCreateDeclaration(_jsTsFunctionRegex, trimmed, index, lines, 1, null, containingSymbol, "function", "javascript", out var functionDeclaration)
                || TryCreateDeclaration(_jsTsVariableFunctionRegex, trimmed, index, lines, 1, null, containingSymbol, "function", "javascript", out functionDeclaration))
            {
                results.Add(functionDeclaration!);
                continue;
            }

            if (startDepth >= 1 && containers.Count > 0
                && TryCreateMemberDeclaration(_jsTsMethodRegex, trimmed, index, lines, containingSymbol, "method", "javascript", out var methodDeclaration))
            {
                results.Add(methodDeclaration!);
            }
        }

        return results;
    }

    private List<HeuristicDeclaration> ParsePythonDeclarations(string[] lines)
    {
        var results = new List<HeuristicDeclaration>();
        var containers = new Stack<(string Name, int Indent)>();
        var inTripleString = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (HandlePythonMultilineString(line, ref inTripleString))
            {
                continue;
            }

            if (ShouldSkipLine(trimmed, "python"))
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            while (containers.Count > 0 && indent <= containers.Peek().Indent)
            {
                containers.Pop();
            }

            var containingSymbol = containers.Count > 0 ? containers.Peek().Name : "(global)";
            if (TryCreateSimpleDeclaration(_pythonClassRegex, trimmed, index, lines, containingSymbol, "class", "python", out var classDeclaration))
            {
                results.Add(classDeclaration!);
                containers.Push((classDeclaration!.Name, indent));
                continue;
            }

            if (TryCreateSimpleDeclaration(_pythonFunctionRegex, trimmed, index, lines, containingSymbol, "function", "python", out var functionDeclaration))
            {
                results.Add(functionDeclaration!);
                containers.Push((functionDeclaration!.Name, indent));
                continue;
            }

            if (indent == 0 && TryCreateSimpleDeclaration(_pythonAssignmentRegex, trimmed, index, lines, containingSymbol, "variable", "python", out var assignmentDeclaration))
            {
                results.Add(assignmentDeclaration!);
            }
        }

        return results;
    }

    private List<HeuristicDeclaration> ParseJavaDeclarations(string[] lines)
    {
        var results = new List<HeuristicDeclaration>();
        var containers = new Stack<(string Name, int Depth)>();
        var braceDepth = 0;
        var inBlockComment = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (HandleBlockComments(trimmed, ref inBlockComment))
            {
                continue;
            }

            var startDepth = braceDepth;
            UpdateBraceDepth(line, ref braceDepth);
            while (containers.Count > 0 && containers.Peek().Depth >= braceDepth)
            {
                containers.Pop();
            }

            if (ShouldSkipLine(trimmed, "java"))
            {
                continue;
            }

            var containingSymbol = containers.Count > 0 ? containers.Peek().Name : "(global)";
            if (TryCreateDeclaration(_javaTypeRegex, trimmed, index, lines, 2, 1, containingSymbol, "type", "java", out var typeDeclaration))
            {
                results.Add(typeDeclaration!);
                containers.Push((typeDeclaration!.Name, Math.Max(startDepth, 0)));
                continue;
            }

            if (startDepth >= 1 && containers.Count > 0
                && TryCreateMemberDeclaration(_javaMethodRegex, trimmed, index, lines, containingSymbol, "method", "java", out var methodDeclaration))
            {
                results.Add(methodDeclaration!);
            }
        }

        return results;
    }

    private List<HeuristicDeclaration> ParseGoDeclarations(string[] lines)
    {
        var results = new List<HeuristicDeclaration>();
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (ShouldSkipLine(trimmed, "go"))
            {
                continue;
            }

            if (TryCreateSimpleDeclaration(_goFunctionRegex, trimmed, index, lines, "(global)", "function", "go", out var functionDeclaration)
                || TryCreateSimpleDeclaration(_goTypeRegex, trimmed, index, lines, "(global)", "type", "go", out functionDeclaration)
                || TryCreateSimpleDeclaration(_goVarConstRegex, trimmed, index, lines, "(global)", "variable", "go", out functionDeclaration))
            {
                results.Add(functionDeclaration!);
            }
        }

        return results;
    }

    private List<HeuristicDeclaration> ParseRustDeclarations(string[] lines)
    {
        var results = new List<HeuristicDeclaration>();
        var containers = new Stack<(string Name, int Depth)>();
        var braceDepth = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            var startDepth = braceDepth;
            UpdateBraceDepth(line, ref braceDepth);
            while (containers.Count > 0 && containers.Peek().Depth >= braceDepth)
            {
                containers.Pop();
            }

            if (ShouldSkipLine(trimmed, "rust"))
            {
                continue;
            }

            var containingSymbol = containers.Count > 0 ? containers.Peek().Name : "(global)";
            if (TryCreateDeclaration(_rustTypeRegex, trimmed, index, lines, 2, 1, containingSymbol, "type", "rust", out var typeDeclaration))
            {
                results.Add(typeDeclaration!);
                containers.Push((typeDeclaration!.Name, Math.Max(startDepth, 0)));
                continue;
            }

            if (_rustImplRegex.IsMatch(trimmed))
            {
                var implMatch = _rustImplRegex.Match(trimmed);
                containers.Push((implMatch.Groups[1].Value, Math.Max(startDepth, 0)));
                continue;
            }

            if (TryCreateSimpleDeclaration(_rustFunctionRegex, trimmed, index, lines, containingSymbol, containers.Count > 0 ? "method" : "function", "rust", out var functionDeclaration))
            {
                results.Add(functionDeclaration!);
            }
        }

        return results;
    }

    private List<HeuristicDeclaration> ParseCFamilyDeclarations(string[] lines, string languageId)
    {
        var results = new List<HeuristicDeclaration>();
        var containers = new Stack<(string Name, int Depth)>();
        var braceDepth = 0;
        var inBlockComment = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (HandleBlockComments(trimmed, ref inBlockComment))
            {
                continue;
            }

            var startDepth = braceDepth;
            UpdateBraceDepth(line, ref braceDepth);

            while (containers.Count > 0 && containers.Peek().Depth >= braceDepth)
            {
                containers.Pop();
            }

            if (ShouldSkipLine(trimmed, languageId))
            {
                continue;
            }

            var containingSymbol = containers.Count > 0 ? containers.Peek().Name : "(global)";
            if ((languageId == "cpp" && TryCreateDeclaration(_cppClassRegex, trimmed, index, lines, 2, 1, containingSymbol, "type", languageId, out var cppTypeDeclaration))
                || TryCreateDeclaration(_cTypeRegex, trimmed, index, lines, 3, 2, containingSymbol, "type", languageId, out cppTypeDeclaration))
            {
                results.Add(cppTypeDeclaration!);
                containers.Push((cppTypeDeclaration!.Name, Math.Max(startDepth, 0)));
                continue;
            }

            if (_cFamilyFunctionRegex.IsMatch(trimmed))
            {
                var match = _cFamilyFunctionRegex.Match(trimmed);
                var name = match.Groups[1].Value;
                var baseName = name.Contains("::", StringComparison.Ordinal) ? name.Split("::").Last() : name;
                if (!_controlKeywords.Contains(baseName, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(CreateDeclaration(baseName, containers.Count > 0 ? "method" : "function", trimmed, containingSymbol, index, lines, languageId));
                }
            }
        }

        return results;
    }

    private static bool HandleBlockComments(string trimmed, ref bool inBlockComment)
    {
        if (inBlockComment)
        {
            if (trimmed.Contains("*/", StringComparison.Ordinal))
            {
                inBlockComment = false;
            }

            return true;
        }

        if (trimmed.StartsWith("/*", StringComparison.Ordinal) && !trimmed.Contains("*/", StringComparison.Ordinal))
        {
            inBlockComment = true;
            return true;
        }

        return false;
    }

    private static bool HandlePythonMultilineString(string line, ref bool inTripleString)
    {
        var tripleCount = CountOccurrences(line, "\"\"\"") + CountOccurrences(line, "'''");
        if (inTripleString)
        {
            if (tripleCount % 2 == 1)
            {
                inTripleString = false;
            }

            return true;
        }

        if (tripleCount % 2 == 1)
        {
            inTripleString = true;
            return true;
        }

        return false;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static bool TryCreateDeclaration(Regex regex,
                                             string trimmed,
                                             int index,
                                             string[] lines,
                                             int nameGroup,
                                             int? kindGroup,
                                             string containingSymbol,
                                             string defaultKind,
                                             string languageId,
                                             out HeuristicDeclaration? declaration)
    {
        declaration = null;
        var match = regex.Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        var name = match.Groups[nameGroup].Value;
        if (_controlKeywords.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var kind = kindGroup is null ? defaultKind : NormalizeKind(match.Groups[kindGroup.Value].Value, defaultKind);
        declaration = CreateDeclaration(name, kind, trimmed, containingSymbol, index, lines, languageId);
        return true;
    }

    private static bool TryCreateMemberDeclaration(Regex regex,
                                                   string trimmed,
                                                   int index,
                                                   string[] lines,
                                                   string containingSymbol,
                                                   string kind,
                                                   string languageId,
                                                   out HeuristicDeclaration? declaration)
    {
        declaration = null;
        var match = regex.Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        var name = match.Groups[1].Value;
        if (_controlKeywords.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        declaration = CreateDeclaration(name, kind, trimmed, containingSymbol, index, lines, languageId);
        return true;
    }

    private static bool TryCreateSimpleDeclaration(Regex regex,
                                                   string trimmed,
                                                   int index,
                                                   string[] lines,
                                                   string containingSymbol,
                                                   string kind,
                                                   string languageId,
                                                   out HeuristicDeclaration? declaration)
    {
        declaration = null;
        var match = regex.Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        var name = match.Groups[1].Value;
        declaration = CreateDeclaration(name, kind, trimmed, containingSymbol, index, lines, languageId);
        return true;
    }

    private static HeuristicDeclaration CreateDeclaration(string name,
                                                          string kind,
                                                          string trimmed,
                                                          string containingSymbol,
                                                          int index,
                                                          string[] lines,
                                                          string languageId)
    {
        var startLine = index + 1;
        var endLine = DetermineBodyEndLine(lines, startLine, languageId);
        return new HeuristicDeclaration(name, kind, CleanSignature(trimmed), containingSymbol, startLine, endLine);
    }

    private static string NormalizeKind(string value, string fallback)
    {
        return value.ToLowerInvariant() switch
        {
            "class" => "class",
            "interface" => "interface",
            "enum" => "enum",
            "record" => "record",
            "struct" => "struct",
            "namespace" => "namespace",
            "trait" => "trait",
            _ => fallback
        };
    }

    private static void UpdateBraceDepth(string line, ref int braceDepth)
    {
        foreach (var character in line)
        {
            if (character == '{')
            {
                braceDepth++;
            }
            else if (character == '}')
            {
                braceDepth--;
            }
        }

        if (braceDepth < 0)
        {
            braceDepth = 0;
        }
    }

    private static bool ShouldSkipLine(string trimmed, string languageId)
    {
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return true;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal)
            || trimmed.StartsWith("#region", StringComparison.Ordinal))
        {
            return true;
        }

        return languageId switch
        {
            "javascript" or "typescript" => trimmed.StartsWith("import ", StringComparison.Ordinal)
                || trimmed.StartsWith("export {", StringComparison.Ordinal),
            "python" => trimmed.StartsWith("#", StringComparison.Ordinal)
                || trimmed.StartsWith("from ", StringComparison.Ordinal)
                || trimmed.StartsWith("import ", StringComparison.Ordinal),
            "java" => trimmed.StartsWith("import ", StringComparison.Ordinal)
                || trimmed.StartsWith("package ", StringComparison.Ordinal)
                || trimmed.StartsWith("@", StringComparison.Ordinal),
            "go" => trimmed.StartsWith("package ", StringComparison.Ordinal)
                || trimmed.StartsWith("import ", StringComparison.Ordinal),
            "rust" => trimmed.StartsWith("use ", StringComparison.Ordinal)
                || trimmed.StartsWith("#[", StringComparison.Ordinal),
            "c" or "cpp" => trimmed.StartsWith("#", StringComparison.Ordinal),
            _ => false
        };
    }

    private static int DetermineBodyEndLine(string[] lines, int startLine, string languageId)
    {
        if (startLine < 1 || startLine > lines.Length)
        {
            return startLine;
        }

        return languageId switch
        {
            "python" => DeterminePythonEndLine(lines, startLine),
            _ => DetermineBraceOrSemicolonEndLine(lines, startLine)
        };
    }

    private static int DeterminePythonEndLine(string[] lines, int startLine)
    {
        var declarationIndent = GetIndent(lines[startLine - 1]);
        for (var lineIndex = startLine; lineIndex < lines.Length; lineIndex++)
        {
            var trimmed = lines[lineIndex].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var indent = GetIndent(lines[lineIndex]);
            if (indent <= declarationIndent && !trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                return lineIndex;
            }
        }

        return lines.Length;
    }

    private static int DetermineBraceOrSemicolonEndLine(string[] lines, int startLine)
    {
        var braceBalance = 0;
        var foundBrace = false;

        for (var lineIndex = startLine - 1; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            foreach (var character in line)
            {
                if (character == '{')
                {
                    braceBalance++;
                    foundBrace = true;
                }
                else if (character == '}')
                {
                    braceBalance--;
                }
            }

            if (!foundBrace && line.TrimEnd().EndsWith(';'))
            {
                return lineIndex + 1;
            }

            if (foundBrace && braceBalance <= 0)
            {
                return lineIndex + 1;
            }
        }

        return startLine;
    }

    private static int DetermineDeclarationEndLine(string[] lines, int startLine, string languageId)
    {
        if (string.Equals(languageId, "python", StringComparison.OrdinalIgnoreCase))
        {
            return startLine;
        }

        for (var lineIndex = startLine - 1; lineIndex < lines.Length; lineIndex++)
        {
            var trimmed = lines[lineIndex].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.Contains('{') || trimmed.EndsWith(";", StringComparison.Ordinal) || trimmed.EndsWith(":", StringComparison.Ordinal))
            {
                return lineIndex + 1;
            }
        }

        return startLine;
    }

    private static int FindCommentStartLine(string[] lines, int declarationStartLine, string languageId)
    {
        var startLine = declarationStartLine;
        for (var lineIndex = declarationStartLine - 2; lineIndex >= 0; lineIndex--)
        {
            var trimmed = lines[lineIndex].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (startLine == declarationStartLine)
                {
                    continue;
                }

                break;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("#", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("*/", StringComparison.Ordinal)
                || trimmed.StartsWith("@", StringComparison.Ordinal)
                || trimmed.StartsWith("[", StringComparison.Ordinal)
                || trimmed.StartsWith("#[", StringComparison.Ordinal))
            {
                startLine = lineIndex + 1;
                continue;
            }

            if (string.Equals(languageId, "python", StringComparison.OrdinalIgnoreCase)
                && (trimmed.StartsWith("\"\"\"", StringComparison.Ordinal) || trimmed.StartsWith("'''", StringComparison.Ordinal)))
            {
                startLine = lineIndex + 1;
                continue;
            }

            break;
        }

        return startLine;
    }

    private static int GetIndent(string line) => line.Length - line.TrimStart().Length;

    private static bool MatchesSignatureHint(SourceSymbolMatch match, string? signatureHint)
    {
        if (string.IsNullOrWhiteSpace(signatureHint))
        {
            return false;
        }

        return match.Signature.Contains(signatureHint, StringComparison.OrdinalIgnoreCase)
               || match.FullyQualifiedName.Contains(signatureHint, StringComparison.OrdinalIgnoreCase)
               || match.RelativePath.Contains(signatureHint, StringComparison.OrdinalIgnoreCase)
               || match.ContainingSymbol.Contains(signatureHint, StringComparison.OrdinalIgnoreCase);
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

    private static string FindContainingSymbol(IReadOnlyList<SourceSymbolMatch> declarations, int lineNumber)
    {
        var match = declarations
            .Where(declaration => declaration.StartLine <= lineNumber && declaration.EndLine >= lineNumber)
            .OrderByDescending(declaration => declaration.StartLine)
            .FirstOrDefault()
            ?? declarations.Where(declaration => declaration.StartLine <= lineNumber)
                .OrderByDescending(declaration => declaration.StartLine)
                .FirstOrDefault();

        return match?.Signature ?? "(global)";
    }

    private static bool LooksLikeDefinition(string line, string symbolName, string? languageId)
    {
        if (string.IsNullOrWhiteSpace(languageId))
        {
            return false;
        }

        var escapedName = Regex.Escape(symbolName);
        return languageId switch
        {
            "javascript" or "typescript" => Regex.IsMatch(line, $@"\b(class|interface|type|enum|function)\s+{escapedName}\b")
                || Regex.IsMatch(line, $@"\b(const|let|var)\s+{escapedName}\b"),
            "python" => Regex.IsMatch(line, $@"\b(class|def)\s+{escapedName}\b") || Regex.IsMatch(line, $@"^{escapedName}\s*=", RegexOptions.IgnoreCase),
            "java" => Regex.IsMatch(line, $@"\b(class|interface|enum|record|@interface)\s+{escapedName}\b"),
            "go" => Regex.IsMatch(line, $@"\b(type|func|var|const)\s+{escapedName}\b"),
            "rust" => Regex.IsMatch(line, $@"\b(struct|enum|trait|type|mod|fn|const|static)\s+{escapedName}\b"),
            "c" or "cpp" => Regex.IsMatch(line, $@"\b(class|struct|enum|union|namespace)\s+{escapedName}\b")
                || Regex.IsMatch(line, $@"\b{escapedName}\s*\("),
            _ => false
        };
    }

    private static string ClassifyReference(string line, SourceSymbolMatch target)
    {
        var trimmed = line.Trim();
        if (Regex.IsMatch(trimmed, @"^(import|from|using|use|require|#include)\b", RegexOptions.IgnoreCase))
        {
            return "import";
        }

        if ((target.Kind == "function" || target.Kind == "method")
            && Regex.IsMatch(trimmed, $@"\b{Regex.Escape(target.Name)}\s*\("))
        {
            return "call";
        }

        if ((target.Kind == "class" || target.Kind == "struct" || target.Kind == "type")
            && Regex.IsMatch(trimmed, $@"\bnew\s+{Regex.Escape(target.Name)}\s*\("))
        {
            return "construction";
        }

        if ((target.Kind == "class" || target.Kind == "interface" || target.Kind == "trait")
            && Regex.IsMatch(trimmed, $@"\b(extends|implements|:|for)\b.*\b{Regex.Escape(target.Name)}\b"))
        {
            return target.Kind == "interface" || target.Kind == "trait" ? "implementation" : "inheritance";
        }

        if (Regex.IsMatch(trimmed, $@"\b{Regex.Escape(target.Name)}\b\s*[<>,*&\[\]]*\s+[A-Za-z_][\w]*"))
        {
            return "type reference";
        }

        return "reference";
    }

    private List<HeuristicTestCase> ParseTestCases(string languageId, string[] lines)
    {
        var results = new List<HeuristicTestCase>();
        switch (languageId)
        {
            case "javascript":
            case "typescript":
                for (var index = 0; index < lines.Length; index++)
                {
                    var match = _jsTestCaseRegex.Match(lines[index]);
                    if (match.Success)
                    {
                        results.Add(new HeuristicTestCase(match.Groups[1].Value, index + 1));
                    }
                }
                break;
            case "python":
                for (var index = 0; index < lines.Length; index++)
                {
                    var match = _pythonTestFunctionRegex.Match(lines[index]);
                    if (match.Success)
                    {
                        results.Add(new HeuristicTestCase(match.Groups[1].Value, index + 1));
                    }
                }
                break;
            case "java":
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!lines[index].Contains("@Test", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    for (var lookAhead = index + 1; lookAhead < Math.Min(lines.Length, index + 6); lookAhead++)
                    {
                        var methodMatch = _javaTestMethodRegex.Match(lines[lookAhead]);
                        if (methodMatch.Success)
                        {
                            results.Add(new HeuristicTestCase(methodMatch.Groups[1].Value, lookAhead + 1));
                            break;
                        }
                    }
                }
                break;
            case "go":
                for (var index = 0; index < lines.Length; index++)
                {
                    var match = _goTestCaseRegex.Match(lines[index]);
                    if (match.Success)
                    {
                        results.Add(new HeuristicTestCase(match.Groups[1].Value, index + 1));
                    }
                }
                break;
            case "rust":
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!lines[index].Contains("#[test]", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    for (var lookAhead = index + 1; lookAhead < Math.Min(lines.Length, index + 6); lookAhead++)
                    {
                        var functionMatch = _rustTestCaseRegex.Match(lines[lookAhead]);
                        if (functionMatch.Success)
                        {
                            results.Add(new HeuristicTestCase(functionMatch.Groups[1].Value, lookAhead + 1));
                            break;
                        }
                    }
                }
                break;
            case "c":
            case "cpp":
                for (var index = 0; index < lines.Length; index++)
                {
                    var match = _cppTestCaseRegex.Match(lines[index]);
                    if (match.Success)
                    {
                        results.Add(new HeuristicTestCase(match.Groups[1].Value.Replace(",", " ", StringComparison.Ordinal), index + 1));
                    }
                }
                break;
        }

        return results;
    }

    private static bool FileNameLooksRelated(string filePath, string symbolName, string? ownerName)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return (fileName.Contains(symbolName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(ownerName) && fileName.Contains(ownerName, StringComparison.OrdinalIgnoreCase)))
            && (fileName.Contains("test", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("spec", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetOwnerTypeName(SourceSymbolMatch target)
    {
        if (target.Kind is "class" or "interface" or "struct" or "enum" or "trait" or "record" or "type")
        {
            return target.Name;
        }

        if (string.IsNullOrWhiteSpace(target.ContainingSymbol) || target.ContainingSymbol == "(global)")
        {
            return null;
        }

        return target.ContainingSymbol.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    private static int CountMentions(string text, string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            return 0;
        }

        return Regex.Matches(text, $@"\b{Regex.Escape(symbolName)}\b").Count;
    }

    private static string GetWindowText(string[] lines, int lineNumber, int radius)
    {
        var start = Math.Max(1, lineNumber - radius);
        var end = Math.Min(lines.Length, lineNumber + radius);
        return string.Join(Environment.NewLine, Enumerable.Range(start, end - start + 1).Select(index => lines[index - 1]));
    }

    private static string GetDefaultTestFramework(string languageId)
    {
        return languageId switch
        {
            "javascript" or "typescript" => "js-test",
            "python" => "pytest",
            "java" => "JUnit",
            "go" => "Go test",
            "rust" => "Rust test",
            "c" or "cpp" => "gtest/catch2",
            _ => "unknown"
        };
    }

    private static string CleanSignature(string line)
    {
        var signature = line.Trim();
        if (signature.EndsWith('{'))
        {
            signature = signature[..^1].TrimEnd();
        }

        return signature;
    }

    private sealed record HeuristicDeclaration(string Name,
                                               string Kind,
                                               string Signature,
                                               string ContainingSymbol,
                                               int StartLine,
                                               int EndLine);

    private sealed record HeuristicTestCase(string Name, int LineNumber);
}

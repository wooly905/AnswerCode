using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AnswerCode.Services.Analysis;

public class TestDiscoveryService(ICSharpCompilationService compilationService,
                                  ILanguageHeuristicService languageHeuristicService,
                                  ISymbolAnalysisService symbolAnalysisService,
                                  IReferenceAnalysisService referenceAnalysisService,
                                  IWorkspaceFileService workspaceFileService) : ITestDiscoveryService
{
    private static readonly HashSet<string> _testAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fact",
        "Theory",
        "Test",
        "TestCase",
        "TestMethod",
        "DataTestMethod"
    };

    public async Task<TestDiscoveryResult> FindTestsAsync(string rootPath,
                                                          string? symbol = null,
                                                          string? filePath = null,
                                                          string? testFramework = null,
                                                          CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(filePath))
        {
            return new TestDiscoveryResult(string.Empty, [], [], [], "Either symbol or file_path is required.");
        }

        var resolvedTargets = await ResolveTargetsAsync(rootPath, symbol, filePath, cancellationToken);
        if (resolvedTargets.Message is not null || resolvedTargets.Targets.Count == 0)
        {
            return resolvedTargets;
        }

        if (resolvedTargets.Targets.Any(target => !string.Equals(target.Language, "csharp", StringComparison.OrdinalIgnoreCase)
            || target.Symbol is null))
        {
            var heuristicMatches = await languageHeuristicService.FindTestsAsync(rootPath, resolvedTargets.Targets, testFramework, cancellationToken);
            var heuristicMessage = heuristicMatches.Count == 0 ? $"No tests found for '{resolvedTargets.Query}'." : null;
            return new TestDiscoveryResult(resolvedTargets.Query, resolvedTargets.Targets, resolvedTargets.Candidates, heuristicMatches, heuristicMessage);
        }

        var projectFrameworks = DiscoverProjectFrameworks(rootPath, testFramework);
        var compilationContext = await compilationService.GetCompilationAsync(rootPath, cancellationToken);
        var candidateTestFiles = compilationContext.SourceFiles
            .Where(path => workspaceFileService.IsTestFile(path) || projectFrameworks.Any(framework => IsUnderDirectory(path, framework.ProjectDirectory)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var referenceIndex = await BuildReferenceIndexAsync(rootPath, resolvedTargets.Targets, cancellationToken);
        var matches = new List<TestFileMatch>();

        foreach (var candidateFile in candidateTestFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = workspaceFileService.ToRelativePath(rootPath, candidateFile);
            if (!compilationContext.PathToTree.TryGetValue(candidateFile, out var syntaxTree))
            {
                continue;
            }

            var root = await syntaxTree.GetRootAsync(cancellationToken);
            var semanticModel = compilationContext.Compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: true);
            var testMethods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => HasTestAttribute(method, semanticModel))
                .ToList();

            if (testMethods.Count == 0)
            {
                continue;
            }

            var fileReasons = new List<string>();
            var fileScore = 0;
            var inferredFramework = ResolveFramework(projectFrameworks, candidateFile, testMethods, semanticModel, testFramework);
            var testCaseMatches = new List<TestCaseMatch>();

            foreach (var target in resolvedTargets.Targets)
            {
                var targetSymbol = target.Symbol;
                var typeName = targetSymbol is INamedTypeSymbol namedType ? namedType.Name : targetSymbol?.ContainingType?.Name ?? target.Name;
                var memberName = target.Name;

                if (FileNameMatches(candidateFile, typeName))
                {
                    fileScore += 20;
                    fileReasons.Add($"file name matches '{typeName}'");
                }

                if (referenceIndex.TryGetValue(relativePath, out var fileReferences))
                {
                    fileScore += Math.Min(45, fileReferences.Count * 25);
                    fileReasons.Add("direct symbol references found in test file");
                }

                foreach (var testMethod in testMethods)
                {
                    var methodReasons = new List<string>();
                    var methodScore = 0;
                    var methodName = testMethod.Identifier.ValueText;
                    var lineNumber = testMethod.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var methodEndLine = testMethod.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
                    var containingClass = testMethod.Parent as ClassDeclarationSyntax;

                    if (containingClass is not null && ClassNameMatches(containingClass.Identifier.ValueText, typeName))
                    {
                        methodScore += 20;
                        methodReasons.Add($"test class '{containingClass.Identifier.ValueText}' matches '{typeName}'");
                    }

                    if (methodName.Contains(memberName, StringComparison.OrdinalIgnoreCase)
                        || methodName.Contains(typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        methodScore += 15;
                        methodReasons.Add($"test case name mentions '{memberName}' or '{typeName}'");
                    }

                    if (referenceIndex.TryGetValue(relativePath, out var refsForFile)
                        && refsForFile.Any(reference => reference.ContainingSymbol.Contains(methodName, StringComparison.OrdinalIgnoreCase)
                            || (reference.LineNumber >= lineNumber && reference.LineNumber <= methodEndLine)))
                    {
                        methodScore += 40;
                        methodReasons.Add("test case directly references the target symbol");
                    }

                    if (NamespaceLooksRelated(testMethod, semanticModel, target))
                    {
                        methodScore += 10;
                        methodReasons.Add("namespace closely matches target code");
                    }

                    if (methodScore == 0)
                    {
                        continue;
                    }

                    testCaseMatches.Add(new TestCaseMatch(methodName,
                                                          lineNumber,
                                                          AnalysisFormatting.ConfidenceFromScore(methodScore),
                                                          methodScore,
                                                          methodReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
                }
            }

            if (testCaseMatches.Count == 0 && fileScore == 0)
            {
                continue;
            }

            if (testCaseMatches.Count > 0)
            {
                fileScore += Math.Min(35, testCaseMatches.Max(testCase => testCase.Score));
                fileReasons.Add("contains matching test cases");
            }

            matches.Add(new TestFileMatch(candidateFile,
                                          relativePath,
                                          inferredFramework,
                                          AnalysisFormatting.ConfidenceFromScore(fileScore),
                                          fileScore,
                                          fileReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                                          testCaseMatches.OrderByDescending(testCase => testCase.Score).ThenBy(testCase => testCase.LineNumber).Take(10).ToList()));
        }

        var orderedMatches = matches.OrderByDescending(match => match.Score)
                                    .ThenBy(match => match.RelativePath, StringComparer.OrdinalIgnoreCase)
                                    .Take(20)
                                    .ToList();

        var query = symbol ?? filePath ?? string.Empty;
        var message = orderedMatches.Count == 0 ? $"No tests found for '{query}'." : null;
        return new TestDiscoveryResult(query, resolvedTargets.Targets, resolvedTargets.Candidates, orderedMatches, message);
    }

    private async Task<TestDiscoveryResult> ResolveTargetsAsync(string rootPath,
                                                                string? symbol,
                                                                string? filePath,
                                                                CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var resolved = await symbolAnalysisService.ResolveSymbolAsync(rootPath, symbol, filePath, cancellationToken: cancellationToken);
            if (resolved.IsSuccess && resolved.Match is not null)
            {
                return new TestDiscoveryResult(symbol, [resolved.Match], resolved.Candidates, []);
            }

            var message = resolved.Message;
            if (string.IsNullOrWhiteSpace(message) && resolved.Candidates.Count > 1)
            {
                message = $"Multiple symbol matches found for '{symbol}'. Refine with file_path.";
            }

            return new TestDiscoveryResult(symbol ?? string.Empty, [], resolved.Candidates, [], message);
        }

        var declaredSymbols = await symbolAnalysisService.GetDeclaredSymbolsInFileAsync(rootPath, filePath!, cancellationToken);
        var targets = declaredSymbols
            .Where(match => match.Kind is "class" or "interface" or "struct" or "record" or "enum" or "method")
            .Where(match => !match.IsTestSymbol)
            .OrderBy(match => match.StartLine)
            .Take(5)
            .ToList();

        if (targets.Count == 0)
        {
            return new TestDiscoveryResult(filePath ?? string.Empty, [], [], [], $"No source symbols found in '{filePath}'.");
        }

        return new TestDiscoveryResult(filePath ?? string.Empty, targets, targets, []);
    }

    private async Task<Dictionary<string, List<SymbolReferenceMatch>>> BuildReferenceIndexAsync(string rootPath,
                                                                                                IReadOnlyList<SourceSymbolMatch> targets,
                                                                                                CancellationToken cancellationToken)
    {
        var index = new Dictionary<string, List<SymbolReferenceMatch>>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            var references = await referenceAnalysisService.FindReferencesAsync(rootPath,
                                                                                target.Name,
                                                                                target.FilePath,
                                                                                scope: "tests",
                                                                                signatureHint: target.Signature,
                                                                                cancellationToken: cancellationToken);

            foreach (var reference in references.References)
            {
                if (!index.TryGetValue(reference.RelativePath, out var list))
                {
                    list = [];
                    index[reference.RelativePath] = list;
                }

                list.Add(reference);
            }
        }

        return index;
    }

    private static bool HasTestAttribute(MethodDeclarationSyntax method, SemanticModel semanticModel)
    {
        foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes))
        {
            var name = attribute.Name.ToString();
            if (_testAttributeNames.Contains(name) || _testAttributeNames.Contains(name.Replace("Attribute", string.Empty, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var type = semanticModel.GetTypeInfo(attribute).Type as INamedTypeSymbol;
            if (type is null)
            {
                continue;
            }

            if (_testAttributeNames.Contains(type.Name)
                || (type.Name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase)
                    && _testAttributeNames.Contains(type.Name[..^"Attribute".Length])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FileNameMatches(string filePath, string typeName)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return fileName.Contains(typeName, StringComparison.OrdinalIgnoreCase)
            && (fileName.Contains("test", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("spec", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ClassNameMatches(string className, string typeName)
    {
        return className.Contains(typeName, StringComparison.OrdinalIgnoreCase)
            && (className.Contains("test", StringComparison.OrdinalIgnoreCase)
                || className.Contains("spec", StringComparison.OrdinalIgnoreCase));
    }

    private static bool NamespaceLooksRelated(MethodDeclarationSyntax method, SemanticModel semanticModel, SourceSymbolMatch target)
    {
        var declaringClass = method.Parent as ClassDeclarationSyntax ?? method.Parent?.Parent as ClassDeclarationSyntax;
        if (declaringClass is null)
        {
            return false;
        }

        var declaredType = semanticModel.GetDeclaredSymbol(declaringClass);
        var testNamespace = declaredType?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var targetNamespace = target.Symbol?.ContainingNamespace?.ToDisplayString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(testNamespace) || string.IsNullOrWhiteSpace(targetNamespace))
        {
            return false;
        }

        return testNamespace.Contains(targetNamespace, StringComparison.OrdinalIgnoreCase)
               || targetNamespace.Contains(testNamespace.Replace("Tests", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderDirectory(string filePath, string directory)
    {
        var normalizedFilePath = Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedFilePath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveFramework(IReadOnlyList<TestProjectFramework> projectFrameworks,
                                           string filePath,
                                           IReadOnlyList<MethodDeclarationSyntax> methods,
                                           SemanticModel semanticModel,
                                           string? requestedFramework)
    {
        if (!string.IsNullOrWhiteSpace(requestedFramework))
        {
            return requestedFramework;
        }

        var projectFramework = projectFrameworks.FirstOrDefault(project => IsUnderDirectory(filePath, project.ProjectDirectory));
        if (projectFramework is not null)
        {
            return projectFramework.Framework;
        }

        foreach (var attribute in methods.SelectMany(method => method.AttributeLists).SelectMany(list => list.Attributes))
        {
            var name = attribute.Name.ToString();
            if (name.Contains("Fact", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Theory", StringComparison.OrdinalIgnoreCase))
            {
                return "xUnit";
            }

            if (name.Contains("TestMethod", StringComparison.OrdinalIgnoreCase)
                || name.Contains("DataTestMethod", StringComparison.OrdinalIgnoreCase))
            {
                return "MSTest";
            }

            if (name.Contains("Test", StringComparison.OrdinalIgnoreCase)
                || name.Contains("TestCase", StringComparison.OrdinalIgnoreCase))
            {
                var type = semanticModel.GetTypeInfo(attribute).Type?.ToDisplayString() ?? string.Empty;
                if (type.Contains("NUnit", StringComparison.OrdinalIgnoreCase))
                {
                    return "NUnit";
                }
            }
        }

        return "unknown";
    }

    private IReadOnlyList<TestProjectFramework> DiscoverProjectFrameworks(string rootPath, string? requestedFramework)
    {
        var projectFiles = workspaceFileService.EnumerateProjectFiles(rootPath, "*.csproj");
        var results = new List<TestProjectFramework>();

        foreach (var projectFile in projectFiles)
        {
            if (!workspaceFileService.IsTestProject(projectFile))
            {
                continue;
            }

            var framework = !string.IsNullOrWhiteSpace(requestedFramework)
                ? requestedFramework
                : ReadFrameworkFromProject(projectFile);

            results.Add(new TestProjectFramework(Path.GetDirectoryName(projectFile) ?? rootPath, framework));
        }

        return results;
    }

    private static string ReadFrameworkFromProject(string projectFile)
    {
        try
        {
            var document = XDocument.Load(projectFile);
            var packageReferences = document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase))
                .Select(element => ((string?)element.Attribute("Include") ?? string.Empty).ToLowerInvariant())
                .ToList();

            if (packageReferences.Any(name => name.Contains("xunit", StringComparison.OrdinalIgnoreCase)))
            {
                return "xUnit";
            }

            if (packageReferences.Any(name => name.Contains("nunit", StringComparison.OrdinalIgnoreCase)))
            {
                return "NUnit";
            }

            if (packageReferences.Any(name => name.Contains("mstest", StringComparison.OrdinalIgnoreCase)))
            {
                return "MSTest";
            }
        }
        catch
        {
            // Ignore malformed project files.
        }

        return "unknown";
    }

    private sealed record TestProjectFramework(string ProjectDirectory, string Framework);
}

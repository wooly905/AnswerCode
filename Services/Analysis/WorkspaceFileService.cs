using System.Xml.Linq;

namespace AnswerCode.Services.Analysis;

public class WorkspaceFileService : IWorkspaceFileService
{
    private static readonly Dictionary<string, string> _languageByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".csx"] = "csharp",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".py"] = "python",
        [".pyw"] = "python",
        [".pyi"] = "python",
        [".java"] = "java",
        [".kt"] = "java",
        [".scala"] = "java",
        [".go"] = "go",
        [".rs"] = "rust",
        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".hpp"] = "cpp",
        [".cc"] = "cpp",
        [".hh"] = "cpp",
        [".cxx"] = "cpp",
        [".hxx"] = "cpp"
    };

    private static readonly HashSet<string> _supportedSourceExtensions = _languageByExtension.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> _excludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".hg",
        ".idea",
        ".nuget",
        ".specstory",
        ".svn",
        ".vs",
        ".vscode",
        "bin",
        "build",
        "dist",
        "node_modules",
        "obj",
        "out",
        "packages",
        "target",
        "vendor"
    };

    private static readonly string[] _testFileMarkers = ["test", "tests", "spec", "specs"];

    public IReadOnlyList<string> EnumerateCSharpFiles(string rootPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath);
        var results = EnumerateSupportedSourceFiles(normalizedRoot)
            .Where(path => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        AddGeneratedGlobalUsings(normalizedRoot, results);

        return results.Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                      .ToList();
    }

    public IReadOnlyList<string> EnumerateSupportedSourceFiles(string rootPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath);
        var results = new List<string>();

        TraverseSupportedFiles(normalizedRoot, results);

        return results.Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                      .ToList();
    }

    public IReadOnlyList<string> EnumerateProjectFiles(string rootPath, string searchPattern)
    {
        var normalizedRoot = Path.GetFullPath(rootPath);

        return Directory.EnumerateFiles(normalizedRoot, searchPattern, SearchOption.AllDirectories)
            .Where(path => !IsUnderExcludedDirectory(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string NormalizePath(string rootPath, string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(Path.Combine(rootPath, path));
    }

    public string ToRelativePath(string rootPath, string path)
    {
        return Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(path));
    }

    public string? GetLanguageId(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return _languageByExtension.TryGetValue(extension, out var languageId) ? languageId : null;
    }

    public bool IsTestFile(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (_testFileMarkers.Any(marker => fileName.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var directoryParts = Path.GetDirectoryName(filePath)?
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray() ?? [];

        return directoryParts.Any(part => _testFileMarkers.Any(marker => part.Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }

    public bool IsTestProject(string projectFilePath)
    {
        if (Path.GetFileNameWithoutExtension(projectFilePath).Contains("test", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(projectFilePath).Contains("spec", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var doc = XDocument.Load(projectFilePath);
            return doc
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "PackageReference", StringComparison.OrdinalIgnoreCase))
                .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update") ?? string.Empty)
                .Any(include => include.Contains("xunit", StringComparison.OrdinalIgnoreCase)
                    || include.Contains("nunit", StringComparison.OrdinalIgnoreCase)
                    || include.Contains("mstest", StringComparison.OrdinalIgnoreCase)
                    || include.Contains("fluentassertions", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public bool IsExcludedDirectory(string directoryName) => _excludedDirectories.Contains(directoryName);

    private void Traverse(string currentDirectory, List<string> results, string searchPattern)
    {
        foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
        {
            var name = Path.GetFileName(directory);
            if (IsExcludedDirectory(name))
            {
                continue;
            }

            Traverse(directory, results, searchPattern);
        }

        foreach (var file in Directory.EnumerateFiles(currentDirectory, searchPattern))
        {
            results.Add(Path.GetFullPath(file));
        }
    }

    private void TraverseSupportedFiles(string currentDirectory, List<string> results)
    {
        foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
        {
            var name = Path.GetFileName(directory);
            if (IsExcludedDirectory(name))
            {
                continue;
            }

            TraverseSupportedFiles(directory, results);
        }

        foreach (var file in Directory.EnumerateFiles(currentDirectory))
        {
            if (_supportedSourceExtensions.Contains(Path.GetExtension(file)))
            {
                results.Add(Path.GetFullPath(file));
            }
        }
    }

    private static void AddGeneratedGlobalUsings(string rootPath, List<string> results)
    {
        var objDirectory = Path.Combine(rootPath, "obj");
        if (!Directory.Exists(objDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(objDirectory, "*.GlobalUsings.g.cs", SearchOption.AllDirectories))
        {
            results.Add(Path.GetFullPath(file));
        }
    }

    private bool IsUnderExcludedDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null)
        {
            return false;
        }

        foreach (var part in directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (IsExcludedDirectory(part))
            {
                return true;
            }
        }

        return false;
    }
}

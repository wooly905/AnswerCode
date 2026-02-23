using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnswerCode.Services;

/// <summary>
/// Builds a compact project overview (metadata + directory tree) for the agent's initial context.
/// Extracted from AgentService to keep that class focused on the agent loop.
/// </summary>
public static class ProjectOverviewBuilder
{
    private static readonly HashSet<string> _excludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        "bin",
        "obj",
        "packages",
        ".git",
        ".svn",
        ".hg",
        ".vs",
        ".vscode",
        ".idea",
        "dist",
        "build",
        "out",
        "target",
        "__pycache__",
        ".pytest_cache",
        ".mypy_cache",
        "venv",
        "env",
        "vendor",
        "bower_components",
        ".nuget"
    };

    private static readonly HashSet<string> _codeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csx",
        ".vb",
        ".fs",
        ".js",
        ".jsx",
        ".ts",
        ".tsx",
        ".mjs",
        ".py",
        ".java",
        ".kt",
        ".go",
        ".rs",
        ".c",
        ".cpp",
        ".h",
        ".hpp",
        ".rb",
        ".php",
        ".swift",
        ".sql",
        ".graphql",
        ".json",
        ".xml",
        ".yaml",
        ".yml",
        ".toml",
        ".css",
        ".scss",
        ".html",
        ".cshtml",
        ".razor",
        ".sh",
        ".ps1",
        ".csproj",
        ".sln",
        ".props"
    };

    /// <summary>
    /// Build a compact project overview containing metadata and directory structure.
    /// This is auto-injected into the initial user message so the agent can start
    /// exploring immediately without wasting an iteration on list_directory.
    /// </summary>
    public static string Build(string rootPath)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Project root: {rootPath}");
        sb.AppendLine($"Project name: {Path.GetFileName(rootPath)}");

        BuildProjectMetadata(rootPath, sb);

        sb.AppendLine();
        sb.AppendLine("Directory structure:");
        BuildCompactTree(new DirectoryInfo(rootPath), sb, "  ", maxDepth: 3, currentDepth: 0);

        return sb.ToString();
    }

    private static void BuildProjectMetadata(string rootPath, StringBuilder sb)
    {
        // .NET projects (.csproj)
        try
        {
            var csprojFiles = Directory.GetFiles(rootPath, "*.csproj", SearchOption.TopDirectoryOnly);

            if (csprojFiles.Length > 0)
            {
                var content = File.ReadAllText(csprojFiles[0]);
                var tfm = Regex.Match(content, @"<TargetFramework>(.*?)</TargetFramework>");
                var sdk = Regex.Match(content, @"<Project\s+Sdk=""(.*?)""");
                sb.AppendLine($"Type: .NET ({(tfm.Success ? tfm.Groups[1].Value : "unknown")})");

                if (sdk.Success)
                {
                    sb.AppendLine($"SDK: {sdk.Groups[1].Value}");
                }

                var packages = Regex.Matches(content, @"<PackageReference\s+Include=""([^""]+)""\s+Version=""([^""]+)""");

                if (packages.Count > 0)
                {
                    sb.AppendLine("Dependencies:");
                    foreach (Match m in packages.Cast<Match>().Take(15))
                    {
                        sb.AppendLine($"  - {m.Groups[1].Value} ({m.Groups[2].Value})");
                    }
                }
                return;
            }
        }
        catch { /* continue */ }

        // Node.js (package.json)
        try
        {
            var packageJsonPath = Path.Combine(rootPath, "package.json");
            if (File.Exists(packageJsonPath))
            {
                var content = File.ReadAllText(packageJsonPath);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.TryGetProperty("name", out var name))
                {
                    sb.AppendLine($"Package: {name.GetString()}");
                }

                sb.AppendLine("Type: Node.js");

                if (root.TryGetProperty("dependencies", out var deps))
                {
                    sb.AppendLine("Dependencies:");
                    int count = 0;
                    foreach (var prop in deps.EnumerateObject().Take(15))
                    {
                        sb.AppendLine($"  - {prop.Name} ({prop.Value.GetString()})");
                        count++;
                    }
                }
                return;
            }
        }
        catch { /* continue */ }

        // Python (pyproject.toml, requirements.txt, setup.py)
        try
        {
            if (File.Exists(Path.Combine(rootPath, "pyproject.toml")))
            {
                sb.AppendLine("Type: Python (pyproject.toml)");
                return;
            }

            if (File.Exists(Path.Combine(rootPath, "requirements.txt")))
            {
                sb.AppendLine("Type: Python");
                var reqs = File.ReadAllLines(Path.Combine(rootPath, "requirements.txt"))
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
                    .Take(15);
                if (reqs.Any())
                {
                    sb.AppendLine("Dependencies:");
                    foreach (var r in reqs)
                    {
                        sb.AppendLine($"  - {r.Trim()}");
                    }
                }
                return;
            }
        }
        catch { /* continue */ }

        // Go (go.mod)
        try
        {
            var goModPath = Path.Combine(rootPath, "go.mod");
            if (File.Exists(goModPath))
            {
                var content = File.ReadAllText(goModPath);
                var module = Regex.Match(content, @"^module\s+(.+)$", RegexOptions.Multiline);
                if (module.Success)
                {
                    sb.AppendLine($"Module: {module.Groups[1].Value.Trim()}");
                }

                sb.AppendLine("Type: Go");
                return;
            }
        }
        catch { /* continue */ }

        // Rust (Cargo.toml)
        try
        {
            if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
            {
                sb.AppendLine("Type: Rust (Cargo)");
                return;
            }
        }
        catch { /* continue */ }

        // Java (pom.xml, build.gradle)
        try
        {
            if (File.Exists(Path.Combine(rootPath, "pom.xml")))
            {
                sb.AppendLine("Type: Java (Maven)");
                return;
            }
            if (File.Exists(Path.Combine(rootPath, "build.gradle"))
                || File.Exists(Path.Combine(rootPath, "build.gradle.kts")))
            {
                sb.AppendLine("Type: Java (Gradle)");
                return;
            }
        }
        catch { /* continue */ }
    }

    private static void BuildCompactTree(DirectoryInfo dir,
                                         StringBuilder sb,
                                         string indent,
                                         int maxDepth,
                                         int currentDepth)
    {
        if (currentDepth >= maxDepth || _excludedDirs.Contains(dir.Name))
        {
            return;
        }

        try
        {
            var subDirs = dir.GetDirectories()
                             .Where(d => !_excludedDirs.Contains(d.Name))
                             .OrderBy(d => d.Name)
                             .ToList();

            var files = dir.GetFiles()
                           .Where(f => _codeExtensions.Contains(f.Extension))
                           .OrderBy(f => f.Name)
                           .ToList();

            foreach (var subDir in subDirs)
            {
                sb.AppendLine($"{indent}{subDir.Name}/");
                BuildCompactTree(subDir, sb, indent + "  ", maxDepth, currentDepth + 1);
            }

            foreach (var file in files.Take(30))
            {
                sb.AppendLine($"{indent}  {file.Name}");
            }

            if (files.Count > 30)
            {
                sb.AppendLine($"{indent}  ... and {files.Count - 30} more files");
            }
        }
        catch (UnauthorizedAccessException) { }
    }
}

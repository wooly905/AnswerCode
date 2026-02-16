using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Related files tool — given a file, finds its dependencies (files it imports/uses)
/// and dependents (files that reference its types/exports). Helps the agent quickly
/// understand code relationships without multiple grep + read iterations.
/// </summary>
public class RelatedFilesTool : ITool
{
    public const string ToolName = "get_related_files";

    public string Name => ToolName;

    public string Description =>
"""
Given a file path, find its related files:
(1) dependencies — files/modules it imports or uses, and
(2) dependents — files that reference types or exports defined in this file.
Helps you understand code relationships without multiple grep calls.
""";

    private const int _maxDependents = 20;

    private static readonly HashSet<string> _codeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cjs",
        ".cs",
        ".go",
        ".java",
        ".js",
        ".jsx",
        ".kt",
        ".mjs",
        ".php",
        ".py",
        ".rb",
        ".rs",
        ".scala",
        ".ts",
        ".tsx",
    };

    private static readonly HashSet<string> _excludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "__pycache__",
        "bin",
        "build",
        "dist",
        "node_modules",
        "obj",
        "out",
        "packages",
        "target",
        "vendor",
    };

    public ChatTool GetChatToolDefinition()
    {
        return ChatTool.CreateFunctionTool(
            functionName: Name,
            functionDescription: Description,
            functionParameters: BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    file_path = new
                    {
                        type = "string",
                        description = "Path to the file (absolute or relative to project root)"
                    }
                },
                required = new[] { "file_path" }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var filePath = args.GetProperty("file_path").GetString() ?? "";

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: file_path is required";
        }

        if (!Path.IsPathRooted(filePath))
        {
            filePath = Path.GetFullPath(Path.Combine(context.RootPath, filePath));
        }

        if (!File.Exists(filePath))
        {
            return $"Error: File not found: {filePath}";
        }

        var lines = await File.ReadAllLinesAsync(filePath);
        var relPath = Path.GetRelativePath(context.RootPath, filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        var sb = new StringBuilder();
        sb.AppendLine($"File: {relPath}");
        sb.AppendLine();

        // ── Part 1: Dependencies (what this file imports) ──
        var imports = ext switch
        {
            ".cs" => ParseCSharpImports(lines),
            ".go" => ParseGoImports(lines),
            ".java" or ".kt" or ".scala" => ParseJavaImports(lines),
            ".py" or ".pyw" => ParsePythonImports(lines),
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => ParseTsImports(lines, filePath),
            _ => []
        };

        sb.AppendLine("── Dependencies (this file imports/uses) ──");

        if (imports.Count > 0)
        {
            foreach (string imp in imports)
            {
                sb.AppendLine($"  {imp}");
            }
        }
        else
        {
            sb.AppendLine("  (none detected)");
        }

        // ── Part 2: Dependents (files that reference this file's types) ──
        sb.AppendLine();
        sb.AppendLine("── Dependents (files that reference this file) ──");

        var typeNames = ext switch
        {
            ".cs" => ExtractCSharpTypeNames(lines),
            ".go" => ExtractGoExportNames(lines),
            ".java" or ".kt" or ".scala" => ExtractJavaTypeNames(lines),
            ".py" or ".pyw" => ExtractPythonNames(lines),
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => ExtractTsExportNames(lines),
            _ => new List<string>()
        };

        if (typeNames.Count > 0)
        {
            var dependents = await FindDependents(typeNames, filePath, context.RootPath);
            if (dependents.Count > 0)
            {
                foreach (var dep in dependents)
                {
                    sb.AppendLine($"  {dep}");
                }
            }
            else
            {
                sb.AppendLine("  (no dependents found)");
            }
        }
        else
        {
            sb.AppendLine("  (no exported types detected to search for)");
        }

        return sb.ToString();
    }

    // ── Import Parsers ─────────────────────────────────────────────

    private static readonly Regex _csUsingRegex = new(@"^\s*using\s+(?!static)([\w.]+)\s*;", RegexOptions.Compiled);

    private static List<string> ParseCSharpImports(string[] lines)
    {
        var imports = new List<string>();
        foreach (var line in lines)
        {
            var m = _csUsingRegex.Match(line);
            if (m.Success)
            {
                imports.Add(m.Groups[1].Value);
            }
            // Stop after the using block (first non-using, non-blank, non-comment line)
            var trimmed = line.TrimStart();

            if (!string.IsNullOrWhiteSpace(trimmed)
                && !trimmed.StartsWith("using")
                && !trimmed.StartsWith("//")
                && !trimmed.StartsWith('#')
                && !trimmed.StartsWith("global"))
            {
                break;
            }
        }
        return imports;
    }

    private static readonly Regex _tsImportRegex = new(@"^\s*import\s+.*?\s+from\s+['""]([^'""]+)['""]", RegexOptions.Compiled);
    private static readonly Regex _tsRequireRegex = new(@"require\s*\(\s*['""]([^'""]+)['""]\s*\)", RegexOptions.Compiled);

    private static List<string> ParseTsImports(string[] lines, string filePath)
    {
        var imports = new List<string>();

        foreach (var line in lines)
        {
            var m = _tsImportRegex.Match(line);

            if (m.Success)
            {
                var importPath = m.Groups[1].Value;
                imports.Add(importPath);
                continue;
            }

            m = _tsRequireRegex.Match(line);

            if (m.Success)
            {
                imports.Add(m.Groups[1].Value);
            }
        }
        return imports;
    }

    private static readonly Regex _pyImportRegex = new(@"^\s*(?:from\s+([\w.]+)\s+import|import\s+([\w.]+))", RegexOptions.Compiled);

    private static List<string> ParsePythonImports(string[] lines)
    {
        var imports = new List<string>();

        foreach (var line in lines)
        {
            var m = _pyImportRegex.Match(line);
            if (m.Success)
            {
                var mod = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                imports.Add(mod);
            }
        }
        return imports;
    }

    private static readonly Regex _javaImportRegex = new(@"^\s*import\s+(?:static\s+)?([\w.]+)\s*;", RegexOptions.Compiled);

    private static List<string> ParseJavaImports(string[] lines)
    {
        var imports = new List<string>();
        foreach (var line in lines)
        {
            var m = _javaImportRegex.Match(line);
            if (m.Success)
            {
                imports.Add(m.Groups[1].Value);
            }

            var trimmed = line.TrimStart();
            if (!string.IsNullOrWhiteSpace(trimmed)
                && !trimmed.StartsWith("import")
                && !trimmed.StartsWith("//")
                && !trimmed.StartsWith("package"))
            {
                break;
            }
        }
        return imports;
    }

    private static readonly Regex _goImportRegex = new(@"^\s*""([^""]+)""", RegexOptions.Compiled);

    private static List<string> ParseGoImports(string[] lines)
    {
        var imports = new List<string>();
        bool inImportBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("import ("))
            {
                inImportBlock = true;
                continue;
            }
            if (inImportBlock)
            {
                if (trimmed.StartsWith(')'))
                {
                    inImportBlock = false;
                    continue;
                }
                var m = _goImportRegex.Match(trimmed);
                if (m.Success)
                {
                    imports.Add(m.Groups[1].Value);
                }
            }
            else if (trimmed.StartsWith("import \""))
            {
                var m = _goImportRegex.Match(trimmed["import ".Length..]);
                if (m.Success)
                {
                    imports.Add(m.Groups[1].Value);
                }
            }
        }
        return imports;
    }

    // ── Type/Export Name Extractors ────────────────────────────────

    private static readonly Regex _csTypeNameRegex = new(@"^\s*(?:(?:public|internal|file)\s+)?(?:(?:static|abstract|sealed|partial)\s+)*(?:class|interface|struct|enum|record)\s+(\w+)", RegexOptions.Compiled);

    private static List<string> ExtractCSharpTypeNames(string[] lines)
    {
        var names = new List<string>();
        foreach (var line in lines)
        {
            var m = _csTypeNameRegex.Match(line);
            if (m.Success)
            {
                names.Add(m.Groups[1].Value);
            }
        }
        return names;
    }

    private static readonly Regex _tsExportNameRegex = new(@"^\s*export\s+(?:default\s+)?(?:class|interface|type|enum|function|const|let|var|abstract\s+class)\s+(\w+)", RegexOptions.Compiled);

    private static List<string> ExtractTsExportNames(string[] lines)
    {
        var names = new List<string>();
        foreach (var line in lines)
        {
            var m = _tsExportNameRegex.Match(line);
            if (m.Success)
            {
                names.Add(m.Groups[1].Value);
            }
        }
        return names;
    }

    private static readonly Regex _pyClassDefRegex = new(@"^\s*class\s+(\w+)", RegexOptions.Compiled);

    private static List<string> ExtractPythonNames(string[] lines)
    {
        var names = new List<string>();
        foreach (var line in lines)
        {
            var m = _pyClassDefRegex.Match(line);
            if (m.Success)
            {
                names.Add(m.Groups[1].Value);
            }
        }
        return names;
    }

    private static readonly Regex _javaTypeNameRegex = new(@"^\s*(?:(?:public|private|protected)\s+)?(?:(?:static|abstract|final|sealed)\s+)*(?:class|interface|enum|record|@interface)\s+(\w+)", RegexOptions.Compiled);

    private static List<string> ExtractJavaTypeNames(string[] lines)
    {
        var names = new List<string>();
        foreach (var line in lines)
        {
            var m = _javaTypeNameRegex.Match(line);
            if (m.Success)
            {
                names.Add(m.Groups[1].Value);
            }
        }
        return names;
    }

    private static readonly Regex _goExportRegex = new(@"^\s*(?:type\s+([A-Z]\w*)|func\s+([A-Z]\w*)\s*\()", RegexOptions.Compiled);

    private static List<string> ExtractGoExportNames(string[] lines)
    {
        var names = new List<string>();
        foreach (var line in lines)
        {
            var m = _goExportRegex.Match(line);
            if (m.Success)
            {
                var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                names.Add(name);
            }
        }
        return names;
    }

    // ── Dependent File Search ──────────────────────────────────────

    private async Task<List<string>> FindDependents(List<string> typeNames, string sourceFile, string rootPath)
    {
        var dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            }))
            {
                // Skip self
                if (file.Equals(sourceFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ext = Path.GetExtension(file);
                if (!_codeExtensions.Contains(ext))
                {
                    continue;
                }

                // Skip excluded directories
                var relDir = Path.GetRelativePath(rootPath, Path.GetDirectoryName(file) ?? "");

                if (relDir.Split(Path.DirectorySeparatorChar).Any(d => _excludedDirs.Contains(d)))
                {
                    continue;
                }

                try
                {
                    var content = File.ReadAllText(file);
                    foreach (var typeName in typeNames)
                    {
                        if (content.Contains(typeName, StringComparison.Ordinal))
                        {
                            dependents.Add(Path.GetRelativePath(rootPath, file));
                            break;
                        }
                    }

                    if (dependents.Count >= _maxDependents)
                    {
                        return;
                    }
                }
                catch { /* skip unreadable */ }
            }
        });

        return dependents.OrderBy(d => d).ToList();
    }
}

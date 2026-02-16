using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Find definition tool — locates where a symbol (class, interface, method, function, etc.)
/// is defined in the codebase. More precise and token-efficient than grep_search because
/// it uses language-aware patterns to return only definitions, not usages.
/// </summary>
public class FindDefinitionTool : ITool
{
    public string Name => "find_definition";

    public string Description =>
"""
Find where a symbol (class, interface, method, function, enum, type, etc.) is defined in the codebase.
More precise than grep_search — returns only definition sites, not every usage.
Supports C#, TypeScript/JavaScript, Python, Java, and Go.
Optionally filter by file pattern (e.g. \"*.cs\").
""";

    private const int _maxResults = 20;

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
                    symbol = new
                    {
                        type = "string",
                        description = "The symbol name to find the definition of (e.g. \"AgentService\", \"RunAsync\", \"ITool\")"
                    },
                    include = new
                    {
                        type = "string",
                        description = "File pattern to limit search scope (e.g. \"*.cs\", \"*.ts\"). Optional."
                    }
                },
                required = new[] { "symbol" }
            }));
    }

    /// <summary>
    /// Definition patterns per language — these regex patterns are designed to match
    /// where a symbol is DEFINED (not used/referenced).
    /// {0} is replaced with the escaped symbol name.
    /// </summary>
    private static readonly Dictionary<string, string[]> _definitionPatterns = new()
    {
        {
            "cs", new[]
            {
                @"\b(class|interface|struct|enum|record|delegate)\s+{0}\b",
                @"\b(namespace)\s+[\w.]*\.?{0}\b",
            }
        },
        {
            "ts", new[]
            {
                @"\b(class|interface|type|enum)\s+{0}\b",
                @"\b(function)\s+{0}\b",
                @"\b(const|let|var)\s+{0}\b\s*[=:]",
            }
        },
        {
            "py", new[]
            {
                @"\b(class)\s+{0}\b",
                @"\b(def)\s+{0}\b",
                @"^{0}\s*=",
            }
        },
        {
            "java", new[]
            {
                @"\b(class|interface|enum|record|@interface)\s+{0}\b",
            }
        },
        {
            "go", new[]
            {
                @"\btype\s+{0}\s+",
                @"\bfunc\s+(?:\([^)]*\)\s+)?{0}\s*\(",
                @"\bvar\s+{0}\b",
                @"\bconst\s+{0}\b",
            }
        },
    };

    /// <summary>
    /// Maps file extensions to language keys for DefinitionPatterns
    /// </summary>
    private static readonly Dictionary<string, string> _extensionToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".cs", "cs" }, { ".csx", "cs" },
        { ".ts", "ts" }, { ".tsx", "ts" }, { ".js", "ts" }, { ".jsx", "ts" }, { ".mjs", "ts" },
        { ".py", "py" }, { ".pyw", "py" }, { ".pyi", "py" },
        { ".java", "java" }, { ".kt", "java" }, { ".scala", "java" },
        { ".go", "go" },
    };

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var symbol = args.GetProperty("symbol").GetString() ?? "";
        string? include = args.TryGetProperty("include", out var inc) ? inc.GetString() : null;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return "Error: symbol is required";
        }

        try
        {
            // Build a combined regex pattern for all languages
            var patterns = BuildSearchPatterns(symbol, include);
            var rgPath = FindRipgrep();

            if (!string.IsNullOrEmpty(rgPath))
            {
                return await RunRipgrepAsync(rgPath, patterns, symbol, context.RootPath, include);
            }

            return await RunBuiltInSearchAsync(patterns, symbol, context.RootPath, include);
        }
        catch (Exception ex)
        {
            return $"Error finding definition: {ex.Message}";
        }
    }

    private static List<string> BuildSearchPatterns(string symbol, string? include)
    {
        var escaped = Regex.Escape(symbol);
        var patterns = new List<string>();

        // Determine which language patterns to use based on include filter
        if (!string.IsNullOrWhiteSpace(include))
        {
            var ext = "." + include.TrimStart('*').TrimStart('.');

            if (_extensionToLanguage.TryGetValue(ext, out var lang)
                && _definitionPatterns.TryGetValue(lang, out var langPatterns))
            {
                patterns.AddRange(langPatterns.Select(p => string.Format(p, escaped)));
                return patterns;
            }
        }

        // Use all language patterns
        foreach (var langPatterns in _definitionPatterns.Values)
        {
            patterns.AddRange(langPatterns.Select(p => string.Format(p, escaped)));
        }

        return patterns;
    }

    private async Task<string> RunRipgrepAsync(string rgPath,
                                               List<string> patterns,
                                               string symbol,
                                               string rootPath,
                                               string? include)
    {
        // Combine all patterns with OR
        var combinedPattern = string.Join("|", patterns);

        var rgArgs = new List<string>
        {
            "-nH", "--no-messages", "-i",
            "--field-match-separator=|",
            "-e", combinedPattern
        };

        if (!string.IsNullOrWhiteSpace(include))
        {
            rgArgs.Add("--glob");
            rgArgs.Add(include);
        }

        // Exclude common non-code directories
        foreach (var dir in new[] { "node_modules", "bin", "obj", ".git", "dist", "build", "vendor" })
        {
            rgArgs.Add("--glob");
            rgArgs.Add($"!{dir}/*");
        }

        // Exclude documentation files
        foreach (var ext in new[] { ".md", ".txt", ".rst", ".doc", ".pdf" })
        {
            rgArgs.Add("--glob");
            rgArgs.Add($"!*{ext}");
        }

        rgArgs.Add(rootPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = rgPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in rgArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);

        if (process == null)
        {
            return "Error: failed to start ripgrep";
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (string.IsNullOrWhiteSpace(stdout))
        {
            return $"No definitions found for '{symbol}'.";
        }

        return FormatResults(stdout, symbol, rootPath);
    }

    private async Task<string> RunBuiltInSearchAsync(List<string> patterns,
                                                     string symbol,
                                                     string rootPath,
                                                     string? include)
    {
        var regexes = new List<Regex>();

        foreach (var p in patterns)
        {
            try { regexes.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)); }
            catch { /* skip bad patterns */ }
        }

        var codeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".js",
            ".ts",
            ".tsx",
            ".jsx",
            ".py",
            ".java",
            ".go",
            ".rs",
            ".c",
            ".cpp",
            ".h",
            ".hpp",
            ".rb",
            ".php",
            ".kt",
            ".scala"
        };

        var matches = new List<(string Path, int Line, string Text)>();

        await Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            }))
            {
                var ext = Path.GetExtension(file);
                if (!codeExtensions.Contains(ext))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(include))
                {
                    var filterExt = "." + include.TrimStart('*').TrimStart('.');

                    if (!ext.Equals(filterExt, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                try
                {
                    var fileLines = File.ReadAllLines(file);
                    for (int i = 0; i < fileLines.Length && matches.Count < _maxResults; i++)
                    {
                        if (regexes.Any(r => r.IsMatch(fileLines[i])))
                        {
                            matches.Add((file, i + 1, fileLines[i].Trim()));
                        }
                    }
                }
                catch { /* skip unreadable */ }
            }
        });

        if (matches.Count == 0)
        {
            return $"No definitions found for '{symbol}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {matches.Count} definition(s) for '{symbol}':");
        sb.AppendLine();

        foreach (var m in matches)
        {
            var relPath = Path.GetRelativePath(rootPath, m.Path);
            sb.AppendLine($"{relPath}:{m.Line}");
            sb.AppendLine($"  {m.Text}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatResults(string rgOutput, string symbol, string rootPath)
    {
        var lines = rgOutput.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var results = new List<(string RelPath, int Line, string Text)>();

        foreach (var line in lines)
        {
            var firstSep = line.IndexOf('|');
            if (firstSep <= 0)
            {
                continue;
            }

            var secondSep = line.IndexOf('|', firstSep + 1);
            if (secondSep <= firstSep)
            {
                continue;
            }

            var filePath = line[..firstSep];
            var lineNumStr = line[(firstSep + 1)..secondSep];
            var lineText = line[(secondSep + 1)..].Trim();

            if (!int.TryParse(lineNumStr, out var lineNum))
            {
                continue;
            }

            if (lineText.Length > 200)
            {
                lineText = lineText[..200] + "...";
            }

            var relPath = Path.GetRelativePath(rootPath, filePath);
            results.Add((relPath, lineNum, lineText));

            if (results.Count >= _maxResults)
            {
                break;
            }
        }

        if (results.Count == 0)
        {
            return $"No definitions found for '{symbol}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} definition(s) for '{symbol}':");
        sb.AppendLine();

        foreach (var r in results)
        {
            sb.AppendLine($"{r.RelPath}:{r.Line}");
            sb.AppendLine($"  {r.Text}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? FindRipgrep()
    {
        var rgName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "rg.exe" : "rg";
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, rgName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

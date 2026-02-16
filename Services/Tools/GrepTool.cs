using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Grep tool — searches file contents using regex
/// </summary>
public class GrepTool : ITool
{
    public const string ToolName = "grep_search";

    public string Name => ToolName;

    public string Description =>
        "Search file contents using regular expressions. " +
        "Returns matching lines with file paths and line numbers, sorted by file modification time (newest first). " +
        "Supports full regex syntax (e.g. \"log.*Error\", \"class\\s+Order\"). " +
        "Use the 'include' parameter to filter by file type (e.g. \"*.cs\", \"*.{ts,tsx}\"). " +
        "Use this tool when you need to find code containing specific patterns, class names, function names, or keywords.";

    private const int MaxLineLength = 2000;
    private const int ResultLimit = 100;

    private static readonly HashSet<string> DocumentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".rst", ".doc", ".docx", ".pdf", ".rtf"
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
                    pattern = new
                    {
                        type = "string",
                        description = "The regex pattern to search for in file contents"
                    },
                    include = new
                    {
                        type = "string",
                        description = "File pattern to include in search (e.g. \"*.cs\", \"*.{ts,tsx}\")"
                    }
                },
                required = new[] { "pattern" }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var pattern = args.GetProperty("pattern").GetString() ?? "";
        string? include = args.TryGetProperty("include", out var inc) ? inc.GetString() : null;

        if (string.IsNullOrWhiteSpace(pattern))
            return "Error: pattern is required";

        try
        {
            var rgPath = FindRipgrep();
            if (!string.IsNullOrEmpty(rgPath))
            {
                return await RunRipgrepAsync(rgPath, pattern, context.RootPath, include);
            }
            return await RunBuiltInRegexAsync(pattern, context.RootPath, include);
        }
        catch (Exception ex)
        {
            return $"Error executing grep: {ex.Message}";
        }
    }

    private async Task<string> RunRipgrepAsync(string rgPath, string pattern, string rootPath, string? include)
    {
        var args = new List<string>
        {
            "-nH", "--hidden", "--no-messages",
            "--no-ignore", // Ignore .gitignore to search in folders like project-code
            "--glob", "!**/.git/**", // Explicitly exclude .git
            "--glob", "!**/bin/**",  // Explicitly exclude bin
            "--glob", "!**/obj/**",  // Explicitly exclude obj
            "--field-match-separator=|",
            "-i", // case insensitive
            "--regexp", pattern
        };

        if (!string.IsNullOrWhiteSpace(include))
        {
            args.Add("--glob");
            args.Add(include);
        }

        foreach (var ext in DocumentationExtensions)
        {
            args.Add("--glob");
            args.Add($"!*{ext}");
        }

        args.Add(rootPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = rgPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);
        if (process == null) return "Error: failed to start ripgrep";

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode == 1 || (process.ExitCode == 2 && string.IsNullOrWhiteSpace(stdout)))
            return "No matches found.";

        var matches = new List<(string Path, int Line, string Text, DateTime Mtime)>();
        var lines = stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var firstSep = line.IndexOf('|');
            if (firstSep <= 0) continue;
            var secondSep = line.IndexOf('|', firstSep + 1);
            if (secondSep <= firstSep) continue;

            var filePath = line[..firstSep];
            var lineNumStr = line[(firstSep + 1)..secondSep];
            var lineText = line[(secondSep + 1)..];

            if (!int.TryParse(lineNumStr, out var lineNum)) continue;
            if (lineText.Length > MaxLineLength)
                lineText = lineText[..MaxLineLength] + "...";

            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(filePath); }
            catch { mtime = DateTime.MinValue; }

            matches.Add((filePath, lineNum, lineText, mtime));
        }

        matches.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));

        var truncated = matches.Count > ResultLimit;
        if (truncated) matches = matches.Take(ResultLimit).ToList();

        return FormatResults(matches, rootPath, truncated);
    }

    private async Task<string> RunBuiltInRegexAsync(string pattern, string rootPath, string? include)
    {
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
        catch { regex = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.Compiled); }

        var matches = new List<(string Path, int Line, string Text, DateTime Mtime)>();
        var codeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".go", ".rs",
            ".c", ".cpp", ".h", ".hpp", ".rb", ".php", ".sql", ".json", ".xml",
            ".yaml", ".yml", ".css", ".html", ".cshtml", ".razor", ".sh", ".ps1"
        };

        await Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            }))
            {
                var ext = Path.GetExtension(file);
                if (!codeExtensions.Contains(ext)) continue;
                if (DocumentationExtensions.Contains(ext)) continue;

                try
                {
                    var fileLines = File.ReadAllLines(file);
                    for (int i = 0; i < fileLines.Length && matches.Count < ResultLimit; i++)
                    {
                        if (regex.IsMatch(fileLines[i]))
                        {
                            var text = fileLines[i].Trim();
                            if (text.Length > MaxLineLength)
                                text = text[..MaxLineLength] + "...";

                            DateTime mtime;
                            try { mtime = File.GetLastWriteTimeUtc(file); }
                            catch { mtime = DateTime.MinValue; }

                            matches.Add((file, i + 1, text, mtime));
                        }
                    }
                }
                catch { /* skip unreadable files */ }
            }
        });

        matches.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));
        return FormatResults(matches, rootPath, matches.Count >= ResultLimit);
    }

    private static string FormatResults(List<(string Path, int Line, string Text, DateTime Mtime)> matches,
                                         string rootPath, bool truncated)
    {
        if (matches.Count == 0) return "No matches found.";

        var output = new List<string> { $"Found {matches.Count} matches:" };
        var currentFile = "";

        foreach (var m in matches)
        {
            var relPath = Path.GetRelativePath(rootPath, m.Path);
            if (currentFile != m.Path)
            {
                if (currentFile != "") output.Add("");
                currentFile = m.Path;
                output.Add($"{relPath}:");
            }
            output.Add($"  Line {m.Line}: {m.Text}");
        }

        if (truncated)
        {
            output.Add("");
            output.Add("(Results truncated. Consider a more specific pattern or include filter.)");
        }

        return string.Join("\n", output);
    }

    private static string? FindRipgrep()
    {
        var rgName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "rg.exe" : "rg";
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, rgName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

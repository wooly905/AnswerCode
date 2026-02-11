using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenAI.Chat;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Glob tool — finds files matching a glob pattern
/// </summary>
public class GlobTool : ITool
{
    public string Name => "glob_search";

    public string Description =>
        "Find files by name pattern using glob syntax. " +
        "Returns a list of matching file paths sorted by modification time (newest first). " +
        "Examples: \"**/*.cs\" (all C# files), \"Controllers/*.cs\" (controllers), \"**/Order*.cs\" (files starting with Order). " +
        "Use this tool to find specific files by name before reading them. " +
        "This is faster than grep_search when you know the file name pattern.";

    private const int ResultLimit = 100;

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
                        description = "Glob pattern to match files (e.g. \"**/*.cs\", \"Controllers/*.cs\", \"**/Order*.cs\")"
                    }
                },
                required = new[] { "pattern" }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var pattern = args.GetProperty("pattern").GetString() ?? "";

        if (string.IsNullOrWhiteSpace(pattern))
            return "Error: pattern is required";

        try
        {
            var rgPath = FindRipgrep();
            if (!string.IsNullOrEmpty(rgPath))
            {
                return await RunRipgrepGlobAsync(rgPath, pattern, context.RootPath);
            }
            return RunBuiltInGlob(pattern, context.RootPath);
        }
        catch (Exception ex)
        {
            return $"Error executing glob search: {ex.Message}";
        }
    }

    private async Task<string> RunRipgrepGlobAsync(string rgPath, string pattern, string rootPath)
    {
        var args = new List<string>
        {
            "--files", "--hidden", "--follow",
            "--glob", "!.git/*",
            "--glob", pattern
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = rgPath,
            WorkingDirectory = rootPath,
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

        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            return "No files found.";

        var files = new List<(string Path, DateTime Mtime)>();
        var lines = stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, line));
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(fullPath); }
            catch { mtime = DateTime.MinValue; }
            files.Add((fullPath, mtime));
        }

        files.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));

        var truncated = files.Count > ResultLimit;
        if (truncated) files = files.Take(ResultLimit).ToList();

        return FormatResults(files, rootPath, truncated);
    }

    private string RunBuiltInGlob(string pattern, string rootPath)
    {
        var matcher = new Matcher();
        matcher.AddInclude(pattern);

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(rootPath)));

        var files = new List<(string Path, DateTime Mtime)>();
        foreach (var match in result.Files)
        {
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, match.Path));
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(fullPath); }
            catch { mtime = DateTime.MinValue; }
            files.Add((fullPath, mtime));
        }

        files.Sort((a, b) => b.Mtime.CompareTo(a.Mtime));

        var truncated = files.Count > ResultLimit;
        if (truncated) files = files.Take(ResultLimit).ToList();

        return FormatResults(files, rootPath, truncated);
    }

    private static string FormatResults(List<(string Path, DateTime Mtime)> files, string rootPath, bool truncated)
    {
        if (files.Count == 0) return "No files found.";

        var output = new List<string> { $"Found {files.Count} files:" };
        output.AddRange(files.Select(f => Path.GetRelativePath(rootPath, f.Path)));

        if (truncated)
        {
            output.Add("");
            output.Add("(Results truncated. Consider a more specific pattern.)");
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

using System.Text.Json;
using System.Text.RegularExpressions;
using AnswerCode.Services.Tools;

namespace AnswerCode.Services;

/// <summary>
/// Formats tool call summaries, result summaries, and detail items for the agent UI.
/// Extracted from AgentService to keep that class focused on the agent loop.
/// </summary>
public static class ToolResultFormatter
{
    // ── Tool Call Summary (one-line label shown while tool is running) ──────

    /// <summary>
    /// Format a human-readable one-line summary for a tool call.
    /// </summary>
    public static string FormatToolCallSummary(string toolName, string argsJson, string rootPath)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argsJson);
            return toolName switch
            {
                GrepTool.ToolName => $"pattern={args.GetProperty("pattern").GetString()}" + (args.TryGetProperty("include", out var gi) ? $"  include={gi.GetString()}" : ""),

                GlobTool.ToolName => $"pattern={args.GetProperty("pattern").GetString()}",

                ReadFileTool.ToolName => ExtractFileName(args.GetProperty("file_path").GetString() ?? "", rootPath),

                ListDirectoryTool.ToolName => args.TryGetProperty("path", out var lp) && lp.GetString() is string p && p.Length > 0
                        ? ExtractFileName(p, rootPath)
                        : "(project root)",

                FindDefinitionTool.ToolName => $"symbol={args.GetProperty("symbol").GetString()}" + (args.TryGetProperty("include", out var fi) ? $"  include={fi.GetString()}" : ""),

                FileOutlineTool.ToolName => ExtractFileName(args.GetProperty("file_path").GetString() ?? "", rootPath),

                RelatedFilesTool.ToolName => ExtractFileName(args.GetProperty("file_path").GetString() ?? "", rootPath),

                _ => argsJson.Length > 100 ? argsJson[..100] + "..." : argsJson
            };
        }
        catch
        {
            return argsJson.Length > 100 ? argsJson[..100] + "..." : argsJson;
        }
    }

    // ── Tool Result Summary (short summary after tool completes) ───────────

    /// <summary>
    /// Format a brief result summary for a completed tool call, based on the tool output.
    /// These summaries appear in the UI next to the tool step (e.g. "5 matches in 3 files").
    /// </summary>
    public static string FormatToolResultSummary(string toolName, string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
        {
            return "";
        }

        try
        {
            return toolName switch
            {
                GrepTool.ToolName => ParseGrepResultSummary(toolResult),
                GlobTool.ToolName => ParseGlobResultSummary(toolResult),
                FindDefinitionTool.ToolName => ParseFindDefResultSummary(toolResult),
                ReadFileTool.ToolName => ParseReadFileResultSummary(toolResult),
                ListDirectoryTool.ToolName => ParseListDirResultSummary(toolResult),
                FileOutlineTool.ToolName => ParseOutlineResultSummary(toolResult),
                RelatedFilesTool.ToolName => ParseRelatedResultSummary(toolResult),
                _ => ""
            };
        }
        catch
        {
            return "";
        }
    }

    // ── Detail Items (expandable bullet lists in the UI) ──────────────────

    /// <summary>
    /// Extract structured detail items from a tool result for display as a bullet list.
    /// Returns a label for the section and a list of items.
    /// </summary>
    public static (string? Label, List<string>? Items) ExtractToolDetailItems(string toolName, string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
        {
            return (null, null);
        }

        try
        {
            return toolName switch
            {
                GrepTool.ToolName => ExtractGrepDetailItems(toolResult),
                GlobTool.ToolName => ExtractGlobDetailItems(toolResult),
                FindDefinitionTool.ToolName => ExtractFindDefDetailItems(toolResult),
                ReadFileTool.ToolName => ExtractReadFileDetailItems(toolResult),
                ListDirectoryTool.ToolName => ExtractListDirDetailItems(toolResult),
                FileOutlineTool.ToolName => ExtractOutlineDetailItems(toolResult),
                RelatedFilesTool.ToolName => ExtractRelatedDetailItems(toolResult),
                _ => (null, null)
            };
        }
        catch
        {
            return (null, null);
        }
    }

    // ── Relevant File Tracking ────────────────────────────────────────────

    /// <summary>
    /// Extract file paths accessed by a tool call and add them to the tracking set.
    /// </summary>
    public static void ExtractRelevantFiles(string toolName, string toolArgs, string toolResult, string rootPath, HashSet<string> filesAccessed)
    {
        if (toolName == ReadFileTool.ToolName)
        {
            try
            {
                var args = JsonSerializer.Deserialize<JsonElement>(toolArgs);
                if (args.TryGetProperty("file_path", out var fp))
                {
                    var filePath = fp.GetString();
                    if (!string.IsNullOrWhiteSpace(filePath))
                    {
                        var fullPath = Path.IsPathRooted(filePath)
                            ? filePath
                            : Path.GetFullPath(Path.Combine(rootPath, filePath));
                        filesAccessed.Add(Path.GetRelativePath(rootPath, fullPath));
                    }
                }
            }
            catch { /* ignore */ }
        }
        else if (toolName == GrepTool.ToolName)
        {
            var lines = toolResult.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd();
                if (trimmed.EndsWith(":") && !line.StartsWith(" ") && !line.StartsWith("\t"))
                {
                    if (trimmed.StartsWith("Found ") && trimmed.Contains(" matches:"))
                    {
                        continue;
                    }

                    var relPath = trimmed[..^1];
                    try
                    {
                        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relPath));
                        filesAccessed.Add(Path.GetRelativePath(rootPath, fullPath));
                    }
                    catch { /* ignore */ }
                }
            }
        }
        else if (toolName == GlobTool.ToolName)
        {
            var lines = toolResult.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed)
                    || trimmed == "No files found."
                    || trimmed.StartsWith("Error")
                    || trimmed.StartsWith("(Results truncated"))
                {
                    continue;
                }

                if (trimmed.StartsWith("Found ") && trimmed.Contains(" files:"))
                {
                    continue;
                }

                try
                {
                    var fullPath = Path.GetFullPath(Path.Combine(rootPath, trimmed));
                    filesAccessed.Add(Path.GetRelativePath(rootPath, fullPath));
                }
                catch { /* ignore */ }
            }
        }
    }

    // ── Private Helpers ───────────────────────────────────────────────────

    private static string ExtractFileName(string filePath, string rootPath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return "(unknown)";
        }

        try
        {
            var full = Path.IsPathRooted(filePath) ? filePath : Path.GetFullPath(Path.Combine(rootPath, filePath));
            return Path.GetFileName(full);
        }
        catch
        {
            return Path.GetFileName(filePath);
        }
    }

    // ── Result Summary Parsers ────────────────────────────────────────────

    private static string ParseGrepResultSummary(string result)
    {
        if (result.StartsWith("No matches"))
        {
            return "no matches";
        }

        if (result.StartsWith("Error"))
        {
            return "";
        }

        var matchCount = Regex.Match(result, @"Found (\d+) matches:");
        if (!matchCount.Success)
        {
            return "";
        }

        var lines = result.Split('\n');
        var fileCount = lines.Count(l =>
        {
            var t = l.TrimEnd();
            return t.EndsWith(":") && !t.StartsWith("Found ") && !t.StartsWith(" ") && !t.StartsWith("\t");
        });

        return $"{matchCount.Groups[1].Value} matches in {fileCount} files";
    }

    private static string ParseGlobResultSummary(string result)
    {
        if (result.StartsWith("No files"))
        {
            return "no files found";
        }

        var m = Regex.Match(result, @"Found (\d+) files:");
        return m.Success ? $"{m.Groups[1].Value} files found" : "";
    }

    private static string ParseFindDefResultSummary(string result)
    {
        if (result.StartsWith("No definitions"))
        {
            return result.TrimEnd();
        }

        var m = Regex.Match(result, @"Found (\d+) definition\(s\) for '([^']+)'");
        if (!m.Success)
        {
            return "";
        }

        var lines = result.Split('\n');
        var firstSig = lines
            .Where(l => l.StartsWith("  ") && !string.IsNullOrWhiteSpace(l.Trim()))
            .Select(l => l.Trim())
            .FirstOrDefault();

        var summary = $"{m.Groups[1].Value} definition(s) for '{m.Groups[2].Value}'";
        if (!string.IsNullOrEmpty(firstSig) && firstSig.Length <= 80)
        {
            summary += $": {firstSig}";
        }

        return summary;
    }

    private static string ParseReadFileResultSummary(string result)
    {
        var m = Regex.Match(result, @"File: (.+?) \((\d+) total lines\)");
        return m.Success ? $"{m.Groups[1].Value} ({m.Groups[2].Value} lines)" : "";
    }

    private static string ParseListDirResultSummary(string result)
    {
        var lines = result.Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("Directory:"))
            .ToList();

        return lines.Count > 0 ? $"{lines.Count} items" : "";
    }

    private static string ParseOutlineResultSummary(string result)
    {
        var fileMatch = Regex.Match(result, @"File: (.+?) \((\d+) lines\)");
        var symbolCount = result.Split('\n')
            .Count(l => l.TrimStart().Length > 0
                && Regex.IsMatch(l, @"^\s*\d+:"));

        if (fileMatch.Success)
        {
            return $"{symbolCount} symbols in {fileMatch.Groups[1].Value}";
        }

        return symbolCount > 0 ? $"{symbolCount} symbols" : "";
    }

    private static string ParseRelatedResultSummary(string result)
    {
        var lines = result.Split('\n');
        int depCount = 0, depntCount = 0;
        bool inDeps = false, inDependents = false;

        foreach (var line in lines)
        {
            if (line.Contains("Dependencies"))
            {
                inDeps = true;
                inDependents = false;
                continue;
            }

            if (line.Contains("Dependents"))
            {
                inDeps = false;
                inDependents = true;
                continue;
            }

            var t = line.Trim();
            if (string.IsNullOrWhiteSpace(t) || t.StartsWith("("))
            {
                continue;
            }

            if (inDeps)
            {
                depCount++;
            }

            if (inDependents)
            {
                depntCount++;
            }
        }

        return $"{depCount} dependencies, {depntCount} dependents";
    }

    // ── Detail Item Extractors ────────────────────────────────────────────

    private static (string?, List<string>?) ExtractGrepDetailItems(string result)
    {
        if (result.StartsWith("No matches") || result.StartsWith("Error"))
        {
            return ("Result", new List<string> { result.Split('\n')[0] });
        }

        var lines = result.Split('\n');
        var fileMatchCounts = new List<(string File, int Count)>();
        string? currentFile = null;
        int currentCount = 0;

        foreach (var line in lines)
        {
            var t = line.TrimEnd();

            if (t.EndsWith(":") && !t.StartsWith("Found ") && !t.StartsWith(" ") && !t.StartsWith("\t"))
            {
                if (currentFile != null)
                {
                    fileMatchCounts.Add((currentFile, currentCount));
                }

                currentFile = t[..^1];
                currentCount = 0;
            }
            else if (currentFile != null && t.TrimStart().StartsWith("Line "))
            {
                currentCount++;
            }
        }

        if (currentFile != null)
        {
            fileMatchCounts.Add((currentFile, currentCount));
        }

        if (fileMatchCounts.Count == 0)
        {
            return (null, null);
        }

        var items = fileMatchCounts
            .Select(f => f.Count > 0 ? $"{f.File} ({f.Count} matches)" : f.File)
            .ToList();

        return ("Matched Files", items);
    }

    private static (string?, List<string>?) ExtractGlobDetailItems(string result)
    {
        if (result.StartsWith("No files"))
        {
            return ("Result", new List<string> { "No files found." });
        }

        var lines = result.Split('\n');
        var items = lines
            .Where(l =>
            {
                var t = l.Trim();
                return !string.IsNullOrWhiteSpace(t)
                    && !t.StartsWith("Found ")
                    && !t.StartsWith("(Results truncated")
                    && !t.StartsWith("Error");
            })
            .Select(l => l.Trim())
            .ToList();

        return items.Count > 0 ? ("Found Files", items) : (null, null);
    }

    private static (string?, List<string>?) ExtractFindDefDetailItems(string result)
    {
        if (result.StartsWith("No definitions"))
        {
            return ("Result", new List<string> { result.Split('\n')[0] });
        }

        var lines = result.Split('\n');
        var items = new List<string>();
        string? pendingLocation = null;

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (string.IsNullOrWhiteSpace(t) || t.StartsWith("Found "))
            {
                continue;
            }

            if (!t.StartsWith(" ") && t.Contains(':') && !line.StartsWith("  "))
            {
                pendingLocation = t;
            }
            else if (pendingLocation != null && line.StartsWith("  "))
            {
                items.Add($"{pendingLocation} — {t}");
                pendingLocation = null;
            }
        }

        if (pendingLocation != null)
        {
            items.Add(pendingLocation);
        }

        return items.Count > 0 ? ("Definitions", items) : (null, null);
    }

    private static (string?, List<string>?) ExtractReadFileDetailItems(string result)
    {
        var items = new List<string>();

        var fileMatch = Regex.Match(result, @"File: (.+?) \((\d+) total lines\)");
        if (fileMatch.Success)
        {
            items.Add($"{fileMatch.Groups[1].Value} — {fileMatch.Groups[2].Value} total lines");
        }

        var truncMatch = Regex.Match(result, @"File has (\d+) more lines\. Use offset=(\d+)");
        if (truncMatch.Success)
        {
            items.Add($"Showing up to line {truncMatch.Groups[2].Value}, {truncMatch.Groups[1].Value} more lines remaining");
        }

        var endMatch = Regex.Match(result, @"End of file — (\d+) total lines");
        if (endMatch.Success)
        {
            items.Add("Read complete (end of file)");
        }

        return items.Count > 0 ? ("File Info", items) : (null, null);
    }

    private static (string?, List<string>?) ExtractListDirDetailItems(string result)
    {
        var lines = result.Split('\n');
        var items = lines
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("Directory:"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        return items.Count > 0 ? ("Contents", items) : (null, null);
    }

    private static (string?, List<string>?) ExtractOutlineDetailItems(string result)
    {
        var lines = result.Split('\n');
        var items = lines
            .Where(l => Regex.IsMatch(l, @"^\s*\d+:"))
            .Select(l => l.Trim())
            .ToList();

        return items.Count > 0 ? ("Symbols", items) : (null, null);
    }

    private static (string?, List<string>?) ExtractRelatedDetailItems(string result)
    {
        var lines = result.Split('\n');
        var items = new List<string>();
        bool inDeps = false, inDependents = false;

        foreach (var line in lines)
        {
            if (line.Contains("Dependencies"))
            {
                items.Add("── Dependencies ──");
                inDeps = true;
                inDependents = false;
                continue;
            }

            if (line.Contains("Dependents"))
            {
                items.Add("── Dependents ──");
                inDeps = false;
                inDependents = true;
                continue;
            }

            var t = line.Trim();
            if (string.IsNullOrWhiteSpace(t) || t.StartsWith("File:"))
            {
                continue;
            }

            if ((inDeps || inDependents) && !t.StartsWith("("))
            {
                items.Add(t);
            }
        }

        return items.Count > 0 ? ("Related Files", items) : (null, null);
    }
}

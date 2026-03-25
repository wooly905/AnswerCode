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

                ReadSymbolTool.ToolName => $"symbol={args.GetProperty("symbol").GetString()}" + (args.TryGetProperty("file_path", out var rsp) ? $"  file={ExtractFileName(rsp.GetString() ?? "", rootPath)}" : ""),

                FileOutlineTool.ToolName => ExtractFileName(args.GetProperty("file_path").GetString() ?? "", rootPath),

                FindReferencesTool.ToolName => $"symbol={args.GetProperty("symbol").GetString()}" + (args.TryGetProperty("scope", out var frs) ? $"  scope={frs.GetString()}" : ""),

                FindTestsTool.ToolName => args.TryGetProperty("symbol", out var fts) && !string.IsNullOrWhiteSpace(fts.GetString())
                    ? $"symbol={fts.GetString()}"
                    : args.TryGetProperty("file_path", out var ftp)
                        ? ExtractFileName(ftp.GetString() ?? "", rootPath)
                        : "(unknown)",

                RelatedFilesTool.ToolName => ExtractFileName(args.GetProperty("file_path").GetString() ?? "", rootPath),

                RepoMapTool.ToolName => args.TryGetProperty("scope", out var rms) && !string.IsNullOrWhiteSpace(rms.GetString())
                    ? $"scope={rms.GetString()}"
                    : "(full repository)",

                CallGraphTool.ToolName => $"symbol={args.GetProperty("symbol").GetString()}"
                    + (args.TryGetProperty("direction", out var cgd) && !string.IsNullOrWhiteSpace(cgd.GetString()) ? $"  direction={cgd.GetString()}" : "")
                    + (args.TryGetProperty("depth", out var cgdp) && cgdp.ValueKind == JsonValueKind.Number ? $"  depth={cgdp.GetInt32()}" : ""),

                ConfigLookupTool.ToolName => $"key={args.GetProperty("key").GetString()}",

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
                ReadSymbolTool.ToolName => ParseReadSymbolResultSummary(toolResult),
                ListDirectoryTool.ToolName => ParseListDirResultSummary(toolResult),
                FileOutlineTool.ToolName => ParseOutlineResultSummary(toolResult),
                FindReferencesTool.ToolName => ParseFindReferencesResultSummary(toolResult),
                FindTestsTool.ToolName => ParseFindTestsResultSummary(toolResult),
                RelatedFilesTool.ToolName => ParseRelatedResultSummary(toolResult),
                RepoMapTool.ToolName => ParseRepoMapResultSummary(toolResult),
                CallGraphTool.ToolName => ParseCallGraphResultSummary(toolResult),
                ConfigLookupTool.ToolName => ParseConfigLookupResultSummary(toolResult),
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
                ReadSymbolTool.ToolName => ExtractReadSymbolDetailItems(toolResult),
                ListDirectoryTool.ToolName => ExtractListDirDetailItems(toolResult),
                FileOutlineTool.ToolName => ExtractOutlineDetailItems(toolResult),
                FindReferencesTool.ToolName => ExtractFindReferencesDetailItems(toolResult),
                FindTestsTool.ToolName => ExtractFindTestsDetailItems(toolResult),
                RelatedFilesTool.ToolName => ExtractRelatedDetailItems(toolResult),
                RepoMapTool.ToolName => ExtractRepoMapDetailItems(toolResult),
                CallGraphTool.ToolName => ExtractCallGraphDetailItems(toolResult),
                ConfigLookupTool.ToolName => ExtractConfigLookupDetailItems(toolResult),
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
            var lines = toolResult.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

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
        else if (toolName == CallGraphTool.ToolName)
        {
            // Extract file paths from call graph edge list lines like "  A → B (path/file.cs:42)"
            var lines = toolResult.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var match = Regex.Match(line, @"\(([^:()\r\n]+\.\w+):(\d+)");
                if (!match.Success)
                {
                    continue;
                }

                var relPath = match.Groups[1].Value.Trim();
                try
                {
                    var fullPath = Path.GetFullPath(Path.Combine(rootPath, relPath));
                    filesAccessed.Add(Path.GetRelativePath(rootPath, fullPath));
                }
                catch { /* ignore */ }
            }
        }
        else if (toolName == ConfigLookupTool.ToolName)
        {
            var lines = toolResult.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var match = Regex.Match(line, @"^\s+(\S+\.\w+)(?::line \d+)?\s+\[");
                if (!match.Success)
                {
                    continue;
                }

                var relPath = match.Groups[1].Value;
                try
                {
                    var fullPath = Path.GetFullPath(Path.Combine(rootPath, relPath));
                    filesAccessed.Add(Path.GetRelativePath(rootPath, fullPath));
                }
                catch { /* ignore */ }
            }
        }
        else if (toolName == ReadSymbolTool.ToolName
                 || toolName == FindReferencesTool.ToolName
                 || toolName == FindTestsTool.ToolName)
        {
            var lines = toolResult.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var match = Regex.Match(line, @"^(?<path>[^:()\[\r\n]+\.(?:cs|csproj))(?::(?<line>\d+))?");
                if (!match.Success)
                {
                    continue;
                }

                var relPath = match.Groups["path"].Value.Trim();
                try
                {
                    var fullPath = Path.GetFullPath(Path.Combine(rootPath, relPath));
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
            return t.EndsWith(':') && !t.StartsWith("Found ") && !t.StartsWith(' ') && !t.StartsWith('\t');
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
        var firstSig = lines.Where(l => l.StartsWith("  ") && !string.IsNullOrWhiteSpace(l.Trim()))
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

    private static string ParseReadSymbolResultSummary(string result)
    {
        if (result.StartsWith("Multiple symbol matches") || result.StartsWith("No symbols found") || result.StartsWith("Unable to read symbol"))
        {
            return result.Split('\n')[0].TrimEnd();
        }

        var symbolMatch = Regex.Match(result, @"Symbol: (.+)");
        var fileMatch = Regex.Match(result, @"File: (.+)");

        return symbolMatch.Success && fileMatch.Success
            ? $"{symbolMatch.Groups[1].Value} in {fileMatch.Groups[1].Value}"
            : "";
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
                                .Count(l => l.TrimStart().Length > 0 && Regex.IsMatch(l, @"^\s*\d+:"));

        if (fileMatch.Success)
        {
            return $"{symbolCount} symbols in {fileMatch.Groups[1].Value}";
        }

        return symbolCount > 0 ? $"{symbolCount} symbols" : "";
    }

    private static string ParseFindReferencesResultSummary(string result)
    {
        if (result.StartsWith("No references") || result.StartsWith("Multiple symbol matches") || result.StartsWith("No symbols found"))
        {
            return result.Split('\n')[0].TrimEnd();
        }

        var match = Regex.Match(result, @"Found (\d+) reference\(s\) for '([^']+)'");
        return match.Success ? $"{match.Groups[1].Value} references for '{match.Groups[2].Value}'" : "";
    }

    private static string ParseFindTestsResultSummary(string result)
    {
        if (result.StartsWith("No tests found") || result.StartsWith("No source symbols found") || result.StartsWith("Multiple symbol matches"))
        {
            return result.Split('\n')[0].TrimEnd();
        }

        var match = Regex.Match(result, @"Found (\d+) test match\(es\) for '([^']+)'");
        return match.Success ? $"{match.Groups[1].Value} test matches for '{match.Groups[2].Value}'" : "";
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
            if (string.IsNullOrWhiteSpace(t) || t.StartsWith('('))
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

            if (t.EndsWith(':') && !t.StartsWith("Found ") && !t.StartsWith(' ') && !t.StartsWith('\t'))
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

        var items = fileMatchCounts.ConvertAll(f => f.Count > 0 ? $"{f.File} ({f.Count} matches)" : f.File);

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

            if (!t.StartsWith(' ') && t.Contains(':') && !line.StartsWith("  "))
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

    private static (string?, List<string>?) ExtractReadSymbolDetailItems(string result)
    {
        if (result.StartsWith("Multiple symbol matches") || result.StartsWith("No symbols found") || result.StartsWith("Unable to read symbol"))
        {
            return ("Result", result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(6).ToList());
        }

        var items = new List<string>();
        foreach (var line in result.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Symbol:")
                || trimmed.StartsWith("Kind:")
                || trimmed.StartsWith("File:")
                || trimmed.StartsWith("Declaration lines:")
                || trimmed.StartsWith("Returned lines:"))
            {
                items.Add(trimmed);
            }
        }

        return items.Count > 0 ? ("Symbol Info", items) : (null, null);
    }

    private static (string?, List<string>?) ExtractListDirDetailItems(string result)
    {
        var lines = result.Split('\n');
        var items = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("Directory:"))
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0)
                         .ToList();

        return items.Count > 0 ? ("Contents", items) : (null, null);
    }

    private static (string?, List<string>?) ExtractOutlineDetailItems(string result)
    {
        var lines = result.Split('\n');
        var items = lines.Where(l => Regex.IsMatch(l, @"^\s*\d+:"))
                         .Select(l => l.Trim())
                         .ToList();

        return items.Count > 0 ? ("Symbols", items) : (null, null);
    }

    private static (string?, List<string>?) ExtractFindReferencesDetailItems(string result)
    {
        if (result.StartsWith("No references") || result.StartsWith("Multiple symbol matches") || result.StartsWith("No symbols found"))
        {
            return ("Result", result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(8).ToList());
        }

        var items = result.Split('\n')
                          .Select(line => line.TrimEnd())
                          .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("Found ") && !line.StartsWith("Target:") && !line.StartsWith("Declared at:"))
                          .ToList();

        return items.Count > 0 ? ("References", items) : (null, null);
    }

    private static (string?, List<string>?) ExtractFindTestsDetailItems(string result)
    {
        if (result.StartsWith("No tests found") || result.StartsWith("No source symbols found") || result.StartsWith("Multiple symbol matches"))
        {
            return ("Result", result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(8).ToList());
        }

        var items = result.Split('\n')
                          .Select(line => line.TrimEnd())
                          .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("Found ") && !line.StartsWith("Targets:"))
                          .ToList();

        return items.Count > 0 ? ("Tests", items) : (null, null);
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

    private static string ParseRepoMapResultSummary(string result)
    {
        var moduleMatch = Regex.Match(result, @"Modules \((\d+)\):");
        var depMatch = Regex.Match(result, @"Module Dependencies:");

        if (!moduleMatch.Success)
        {
            return "";
        }

        var summary = $"{moduleMatch.Groups[1].Value} modules";
        if (depMatch.Success)
        {
            var edgeCount = Regex.Matches(result, @"→").Count;
            if (edgeCount > 0)
            {
                summary += $", {edgeCount} dependency edges";
            }
        }

        return summary;
    }

    private static (string?, List<string>?) ExtractRepoMapDetailItems(string result)
    {
        var lines = result.Split('\n');
        var items = new List<string>();

        foreach (var line in lines)
        {
            var t = line.Trim();

            if (t.StartsWith("Modules"))
            {
                items.Add($"── {t} ──");
                continue;
            }

            if (t.StartsWith("Entry Points:") || t.StartsWith("Module Dependencies:") || t.StartsWith("Mermaid:"))
            {
                if (!t.StartsWith("Mermaid:"))
                {
                    items.Add($"── {t.TrimEnd(':')} ──");
                }
                continue;
            }

            if (t.StartsWith("graph ") || t.StartsWith("Repository Map:") || t.StartsWith("Type:") || string.IsNullOrWhiteSpace(t))
            {
                continue;
            }

            if (!t.StartsWith("──") && !string.IsNullOrWhiteSpace(t))
            {
                items.Add(t);
            }
        }

        return items.Count > 0 ? ("Repository Map", items) : (null, null);
    }

    private static string ParseCallGraphResultSummary(string result)
    {
        if (result.StartsWith("Multiple symbol matches") || result.StartsWith("No symbols found") || result.StartsWith("No call graph"))
        {
            return result.Split('\n')[0].TrimEnd();
        }

        var edgeMatch = Regex.Match(result, @"Edges \((\d+)\):");
        var dirMatch = Regex.Match(result, @"Static Call Graph \((\w+)\) for '([^']+)'");

        if (!dirMatch.Success)
        {
            return "";
        }

        var summary = $"{dirMatch.Groups[1].Value} call graph for '{dirMatch.Groups[2].Value}'";
        if (edgeMatch.Success)
        {
            summary += $", {edgeMatch.Groups[1].Value} edges";
        }

        var warningCount = Regex.Matches(result, "⚠").Count;
        if (warningCount > 0)
        {
            summary += $", {warningCount} warning(s)";
        }

        return summary;
    }

    private static (string?, List<string>?) ExtractCallGraphDetailItems(string result)
    {
        if (result.StartsWith("Multiple symbol matches") || result.StartsWith("No symbols found") || result.StartsWith("No call graph"))
        {
            return ("Result", result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(8).ToList());
        }

        var lines = result.Split('\n');
        var items = new List<string>();

        foreach (var line in lines)
        {
            var t = line.TrimEnd();

            // Include tree lines (with ├── └── │ prefixes) and edge lines
            if (t.Contains("├──") || t.Contains("└──") || t.Contains('│'))
            {
                items.Add(t);
                continue;
            }

            // Include edge list lines
            if (t.TrimStart().StartsWith('→') || (t.Contains(" → ") && t.TrimStart().Length > 0 && !t.StartsWith("Static") && !t.StartsWith("Edges")))
            {
                items.Add(t.Trim());
                continue;
            }

            // Include warnings
            if (t.TrimStart().StartsWith('⚠'))
            {
                items.Add(t.Trim());
            }
        }

        return items.Count > 0 ? ("Call Graph", items) : (null, null);
    }

    private static string ParseConfigLookupResultSummary(string result)
    {
        if (result.Contains("was not found"))
        {
            return "key not found";
        }

        var match = Regex.Match(result, @"Found in (\d+) location\(s\)");
        if (!match.Success)
        {
            return "";
        }

        var summary = $"found in {match.Groups[1].Value} location(s)";
        var effectiveMatch = Regex.Match(result, @"Effective value: (.+)");
        if (effectiveMatch.Success)
        {
            var val = effectiveMatch.Groups[1].Value.Trim();
            if (val.Length > 50)
            {
                val = val[..50] + "...";
            }

            summary += $", effective: {val}";
        }

        return summary;
    }

    private static (string?, List<string>?) ExtractConfigLookupDetailItems(string result)
    {
        if (result.Contains("was not found"))
        {
            return ("Result", new List<string> { result.Split('\n')[0] });
        }

        var items = new List<string>();
        foreach (var line in result.Split('\n'))
        {
            var t = line.Trim();
            if (t.Contains(" = ") && !t.StartsWith("(from"))
            {
                items.Add(t);
            }
            else if (t.StartsWith("Effective value:"))
            {
                items.Add($">> {t}");
            }
        }

        return items.Count > 0 ? ("Config Values", items) : (null, null);
    }
}

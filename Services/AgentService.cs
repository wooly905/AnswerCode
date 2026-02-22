using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnswerCode.Models;
using AnswerCode.Services.Providers;
using AnswerCode.Services.Tools;
using OpenAI.Chat;

namespace AnswerCode.Services;

/// <summary>
/// Agent Service — runs an agentic tool-calling loop (like OpenCode) instead of a fixed pipeline.
/// The LLM autonomously decides which tools to use, when to search, and when to stop.
/// </summary>
public class AgentService : IAgentService
{
    private readonly ILogger<AgentService> _logger;
    private readonly ILLMServiceFactory _llmFactory;
    private readonly ToolRegistry _toolRegistry;

    private const int _maxIterations = 50;

    private const string _agentSystemPrompt = @"
You are an expert code analyst and software engineer. Your task is to answer user questions about the codebase using the available tools.

## MANDATORY RULES — NEVER VIOLATE
1. **You MUST call at least one tool before writing any final answer.** No exceptions.
2. **Never answer from your training data or general knowledge.** Every claim must be backed by actual code you have read from this codebase.
3. **Never ask the user clarifying questions.** Start using tools immediately to explore the codebase.
4. **Never say ""Would you like me to search..."" or similar.** Just search.

## Core Philosophy
1. **Read First**: Never analyze or modify code you haven't read. If you need to understand logic, find the file and read it.
2. **Precision**: Cite specific file paths and line numbers in your answers.
3. **Thoroughness**: If a search fails, do not give up. Try broader keywords, synonyms, or related concepts.
4. **Context**: A project overview is provided. Use it to orient yourself, but don't rely on it for deep exploration.

## Tool Usage Guidelines
- **Finding Files**:
  - Use `glob_search` when you have a general idea of the filename (e.g., ""*.config"", ""User*"").
  - Use `grep_search` when you are looking for code logic, specific strings, or variable names.
  - Use `list_directory` ONLY when exploring a specific subdirectory that is truncated in the overview (""... and X more files"").

- **Reading Code**:
  - Use `get_file_outline` first to get a high-level view of large files (classe names, methods).
  - Use `read_file` to examine specific logic. Use `start_line` and `end_line` for large files to save tokens.

- **Understanding Structure**:
  - Use `get_related_files` to understand dependencies (imports/exports) before refactoring or deep analysis.
  - Use `find_definition` to jump to where a symbol is defined.

## Exploration Strategy (When you don't know where to start)
1. **Check the Overview**: Look for high-level folders (Controllers, Services, Src) that match the domain of the question.
2. **Search Concepts**: If no obvious file exists, `grep_search` for the *concept* (e.g., ""tax"", ""auth"", ""retry"").
3. **Follow the Breadcrumbs**:
   - Found a relevant interface? Use `find_definition` to find its implementation.
   - Found a usage? Use `read_file` to see the context.
   - Directory looks relevant but empty in overview? Use `list_directory` to dig deeper.

## Handling Truncated Results
If a tool output is truncated (e.g., ""... 50 more matches"") or the Project Overview shows ""... and X more files"":
- This proves there is more content.
- You MUST narrow your search or list that specific directory to see the hidden files.
- Do NOT assume the hidden files are irrelevant.

## Final Answer
- Summarize what you found.
- If code was found, include the file path and line numbers.
- If no code was found after a thorough search, explain what you searched for and why you think it's missing.
- Respond in the same language as the user's question.
";

    private const string _pmSystemPrompt = @"
You are a knowledgeable business analyst helping a Program Manager (PM) or Project Manager understand a software project. Your task is to answer questions about the codebase in plain, non-technical language using the available tools.

## MANDATORY RULES — NEVER VIOLATE
1. **You MUST call at least one tool before writing any final answer.** No exceptions.
2. **Never answer from your training data or general knowledge.** Every claim must be backed by actual code you have explored in this codebase using tools.
3. **Never ask the user clarifying questions.** Start using tools immediately to explore the codebase.
4. **Never say ""Would you like me to search..."" or similar.** Just search.

## Core Philosophy
1. **Business First**: Focus on what the system does for users and the business, not how it is implemented technically.
2. **No Code**: Never include raw code snippets, method signatures, or class hierarchies in your final answer. Translate everything into business terms.
3. **Workflow Oriented**: Describe processes as step-by-step workflows (e.g., ""When a user submits an order, the system validates payment, then notifies the warehouse..."").
4. **Module Interaction**: Explain how major functional areas (e.g., Authentication, Payment, Notifications) connect and depend on each other in plain language.
5. **Thoroughness**: Use the available tools to fully explore the codebase before answering. Don't guess.

## Tool Usage Guidelines
- Use `grep_search` and `glob_search` to locate relevant files.
- Use `read_file` and `get_file_outline` to understand what logic exists — but translate findings into business language before presenting them.
- Use `get_related_files` and `find_definition` to trace dependencies between modules.
- Use `list_directory` when a directory in the project overview is truncated (shows ""... and X more files""), to ensure no relevant area is missed.

## Exploration Strategy
1. Identify the high-level feature areas related to the question (e.g., user login, order processing).
2. Search for files and code related to those areas.
3. Read and understand the logic, then describe it in business terms.

## Final Answer Format
- Use plain language a non-developer can understand.
- Structure the answer with clear sections: **Overview**, **Business Workflow**, **Module Interactions**, **Key Rules / Business Constraints**.
- Do NOT include file paths, line numbers, class names, or method names in the final answer.
- If no relevant logic was found after a thorough search, say so clearly.
- Respond in the same language as the user's question.
";

    public AgentService(ILogger<AgentService> logger, ILLMServiceFactory llmFactory)
    {
        _logger = logger;
        _llmFactory = llmFactory;

        _toolRegistry = new ToolRegistry();
        _toolRegistry.Register(new GrepTool());
        _toolRegistry.Register(new ReadFileTool());
        _toolRegistry.Register(new ListDirectoryTool());
        _toolRegistry.Register(new GlobTool());
        _toolRegistry.Register(new FileOutlineTool());
        _toolRegistry.Register(new FindDefinitionTool());
        _toolRegistry.Register(new RelatedFilesTool());
    }

    /// <summary>
    /// Run without progress callback (original API)
    /// </summary>
    public Task<AgentResult> RunAsync(string question,
                                      string rootPath,
                                      string? sessionId = null,
                                      string? modelProvider = null,
                                      string? userRole = null)
    {
        return RunAsync(question, rootPath, _ => Task.CompletedTask, sessionId, modelProvider, userRole);
    }

    /// <summary>
    /// Run with progress callback for SSE streaming
    /// </summary>
    public async Task<AgentResult> RunAsync(string question,
                                            string rootPath,
                                            Func<AgentEvent, Task> onProgress,
                                            string? sessionId = null,
                                            string? modelProvider = null,
                                            string? userRole = null)
    {
        _logger.LogInformation("Agent starting for question: {Question}, project: {RootPath}", question, rootPath);

        var provider = _llmFactory.GetProvider(modelProvider);

        // Emit start event
        await onProgress(new AgentEvent { Type = AgentEventType.Started });

        if (!provider.SupportsToolCalling)
        {
            _logger.LogInformation("Provider {Provider} does not support native tool calling, using ReAct agent loop", provider.Name);
            return await RunReActLoopAsync(question, rootPath, provider, onProgress);
        }

        var toolContext = new ToolContext
        {
            RootPath = rootPath,
            Logger = _logger
        };

        var result = new AgentResult();
        var chatTools = _toolRegistry.GetChatToolDefinitions();
        var filesAccessed = new HashSet<string>();

        var projectOverview = BuildProjectOverview(rootPath);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(string.Equals(userRole, "PM", StringComparison.OrdinalIgnoreCase)
                ? _pmSystemPrompt
                : _agentSystemPrompt),
            new UserChatMessage($"## Project Overview\n{projectOverview}\n\n## Question\n{question}")
        };

        for (int iteration = 0; iteration < _maxIterations; iteration++)
        {
            result.IterationCount = iteration + 1;
            _logger.LogInformation("Agent iteration {Iteration}/{Max}", iteration + 1, _maxIterations);

            LLMChatResponse response;
            try
            {
                response = await provider.ChatWithToolsAsync(messages, chatTools);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM call failed at iteration {Iteration}", iteration + 1);
                result.Answer = $"Error communicating with LLM: {ex.Message}";
                await onProgress(new AgentEvent { Type = AgentEventType.Error, Summary = result.Answer });
                return result;
            }

            result.TotalInputTokens += response.InputTokens;
            result.TotalOutputTokens += response.OutputTokens;
            messages.Add(response.AssistantMessage);

            if (!response.IsToolCall)
            {
                result.Answer = response.TextContent ?? "";
                _logger.LogInformation("Agent finished after {Iterations} iterations, {ToolCalls} tool calls", iteration + 1, result.TotalToolCalls);
                break;
            }

            foreach (var toolCall in response.ToolCalls)
            {
                // Parse arguments for a clean display summary
                var argsSummary = FormatToolCallSummary(toolCall.FunctionName, toolCall.Arguments, rootPath);

                // Emit tool start event
                await onProgress(new AgentEvent
                {
                    Type = AgentEventType.ToolCallStart,
                    ToolName = toolCall.FunctionName,
                    ToolArgs = toolCall.Arguments,
                    Summary = argsSummary,
                    Iteration = iteration + 1
                });

                DateTime startTime = DateTime.Now;
                string toolResult;

                var tool = _toolRegistry.GetTool(toolCall.FunctionName);
                if (tool == null)
                {
                    toolResult = $"Error: Unknown tool '{toolCall.FunctionName}'.";
                }
                else
                {
                    try
                    {
                        toolResult = await tool.ExecuteAsync(toolCall.Arguments, toolContext);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Tool {Tool} execution failed", toolCall.FunctionName);
                        toolResult = $"Error executing {toolCall.FunctionName}: {ex.Message}";
                    }
                }

                result.TotalToolCalls++;
                var record = new ToolCallRecord
                {
                    ToolName = toolCall.FunctionName,
                    Arguments = toolCall.Arguments,
                    ResultSummary = toolResult.Length > 500
                        ? toolResult[..500] + $"... ({toolResult.Length} chars total)"
                        : toolResult,
                    DurationMs = (DateTime.Now - startTime).Milliseconds,
                };
                result.ToolCalls.Add(record);

                // Track files accessed
                ExtractRelevantFiles(toolCall.FunctionName, toolCall.Arguments, toolResult, rootPath, filesAccessed);

                messages.Add(new ToolChatMessage(toolCall.CallId, toolResult));

                // Emit tool end event
                var resultSummary = FormatToolResultSummary(toolCall.FunctionName, toolResult);
                var resultDetails = toolResult.Length > 5000
                    ? toolResult[..5000] + "\n... (truncated)"
                    : toolResult;
                var (detailLabel, detailItems) = ExtractToolDetailItems(toolCall.FunctionName, toolResult, rootPath);

                await onProgress(new AgentEvent
                {
                    Type = AgentEventType.ToolCallEnd,
                    ToolName = toolCall.FunctionName,
                    ToolArgs = toolCall.Arguments,
                    Summary = argsSummary,
                    DurationMs = (DateTime.Now - startTime).Milliseconds,
                    TotalToolCalls = result.TotalToolCalls,
                    ResultSummary = resultSummary,
                    ResultDetails = resultDetails,
                    DetailLabel = detailLabel,
                    DetailItems = detailItems
                });

                _logger.LogInformation("Tool {Tool} completed in {Ms}ms, result: {Length} chars",
                                       toolCall.FunctionName,
                                       (DateTime.Now - startTime).Milliseconds,
                                       toolResult.Length);
            }
        }

        if (string.IsNullOrEmpty(result.Answer))
        {
            result.Answer = "I was unable to complete my analysis within the allowed number of iterations. Please try asking a more specific question.";
        }

        result.RelevantFiles = [.. filesAccessed];
        return result;
    }

    private void ExtractRelevantFiles(string toolName, string toolArgs, string toolResult, string rootPath, HashSet<string> filesAccessed)
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
                // GrepTool outputs "RelativePath:"
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
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }
                if (trimmed.StartsWith("Found ") && trimmed.Contains(" files:"))
                {
                    continue;
                }
                if (trimmed == "No files found.")
                {
                    continue;
                }
                if (trimmed.StartsWith("Error"))
                {
                    continue;
                }
                if (trimmed.StartsWith("(Results truncated"))
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

    /// <summary>
    /// Format a human-readable one-line summary for a tool call
    /// </summary>
    private static string FormatToolCallSummary(string toolName, string argsJson, string rootPath)
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

    /// <summary>
    /// Format a brief result summary for a completed tool call, based on the tool output.
    /// These summaries appear in the UI next to the tool step (e.g. "5 matches in 3 files").
    /// </summary>
    private static string FormatToolResultSummary(string toolName, string toolResult)
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

        // Extract first definition signature for extra context
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

    // ── Detail Items Extraction (for expandable bullet lists in the UI) ────

    /// <summary>
    /// Extract structured detail items from a tool result for display as a bullet list.
    /// Returns a label for the section and a list of items.
    /// </summary>
    private static (string? Label, List<string>? Items) ExtractToolDetailItems(
        string toolName, string toolResult, string rootPath)
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

    /// <summary>
    /// Grep: list each matched file with its match count.
    /// e.g. "Services/AgentService.cs (5 matches)"
    /// </summary>
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

            // File header line: "relative/path.cs:"
            if (t.EndsWith(":") && !t.StartsWith("Found ") && !t.StartsWith(" ") && !t.StartsWith("\t"))
            {
                // Save previous file
                if (currentFile != null)
                {
                    fileMatchCounts.Add((currentFile, currentCount));
                }

                currentFile = t[..^1]; // remove trailing ":"
                currentCount = 0;
            }
            else if (currentFile != null && t.TrimStart().StartsWith("Line "))
            {
                currentCount++;
            }
        }

        // Save last file
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

    /// <summary>
    /// Glob: list each found file path.
    /// </summary>
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

    /// <summary>
    /// FindDefinition: list each definition with file:line and signature.
    /// e.g. "Services/AgentService.cs:76 — public class AgentService"
    /// </summary>
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

            // Location line: "relpath.cs:123"
            if (!t.StartsWith(" ") && t.Contains(':') && !line.StartsWith("  "))
            {
                pendingLocation = t;
            }
            // Signature line (indented): "  public class AgentService"
            else if (pendingLocation != null && line.StartsWith("  "))
            {
                items.Add($"{pendingLocation} — {t}");
                pendingLocation = null;
            }
        }

        // Add any leftover location without signature
        if (pendingLocation != null)
        {
            items.Add(pendingLocation);
        }

        return items.Count > 0 ? ("Definitions", items) : (null, null);
    }

    /// <summary>
    /// ReadFile: show file path and line range info.
    /// </summary>
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

    /// <summary>
    /// ListDirectory: list each directory entry.
    /// </summary>
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

    /// <summary>
    /// FileOutline: list each symbol with its line number.
    /// </summary>
    private static (string?, List<string>?) ExtractOutlineDetailItems(string result)
    {
        var lines = result.Split('\n');
        var items = lines
            .Where(l => Regex.IsMatch(l, @"^\s*\d+:"))
            .Select(l => l.Trim())
            .ToList();

        return items.Count > 0 ? ("Symbols", items) : (null, null);
    }

    /// <summary>
    /// RelatedFiles: list dependencies and dependents with sub-labels.
    /// </summary>
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

    // ── Project Overview Builder ───────────────────────────────────────

    /// <summary>
    /// Build a compact project overview containing metadata and directory structure.
    /// This is auto-injected into the initial user message so the agent can start
    /// exploring immediately without wasting an iteration on list_directory.
    /// </summary>
    private string BuildProjectOverview(string rootPath)
    {
        var sb = new StringBuilder();

        // Project name
        sb.AppendLine($"Project root: {rootPath}");
        sb.AppendLine($"Project name: {Path.GetFileName(rootPath)}");

        // Detect project type and parse metadata
        BuildProjectMetadata(rootPath, sb);

        // Compact directory tree
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
        catch
        {
            /* continue */
        }

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
        catch
        {
            /* continue */
        }

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

    private static readonly HashSet<string> _overviewExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
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

    private static readonly HashSet<string> _overviewCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
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

    private static void BuildCompactTree(DirectoryInfo dir,
                                         StringBuilder sb,
                                         string indent,
                                         int maxDepth,
                                         int currentDepth)
    {
        if (currentDepth >= maxDepth || _overviewExcludedDirs.Contains(dir.Name))
        {
            return;
        }

        try
        {
            var subDirs = dir.GetDirectories()
                             .Where(d => !_overviewExcludedDirs.Contains(d.Name))
                             .OrderBy(d => d.Name)
                             .ToList();

            var files = dir.GetFiles()
                           .Where(f => _overviewCodeExtensions.Contains(f.Extension))
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

    /// <summary>
    /// ReAct agent loop — uses text-based tool calling so any LLM can be an agent,
    /// without requiring native tool calling (function calling) support.
    /// The LLM outputs &lt;tool_call&gt; XML tags, which are parsed and executed server-side.
    /// </summary>
    private async Task<AgentResult> RunReActLoopAsync(string question,
                                                      string rootPath,
                                                      ILLMProvider provider,
                                                      Func<AgentEvent, Task> onProgress)
    {
        _logger.LogInformation("Starting ReAct agent loop for provider {Provider}", provider.Name);

        var toolContext = new ToolContext { RootPath = rootPath, Logger = _logger };
        var result = new AgentResult();
        var filesAccessed = new HashSet<string>();

        // Build system prompt with tool descriptions embedded in text
        var toolDescriptions = _toolRegistry.GetReActToolDescriptions();
        var systemPrompt = ReActParser.BuildReActSystemPrompt(toolDescriptions);

        var projectOverview = BuildProjectOverview(rootPath);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage($"## Project Overview\n{projectOverview}\n\n## Question\n{question}")
        };

        for (int iteration = 0; iteration < _maxIterations; iteration++)
        {
            result.IterationCount = iteration + 1;
            _logger.LogInformation("ReAct iteration {Iteration}/{Max}", iteration + 1, _maxIterations);

            // Call LLM (plain text chat, no tool definitions)
            string llmResponse;
            try
            {
                var chatResponse = await provider.ChatAsync(messages);
                llmResponse = chatResponse.TextContent ?? "";
                result.TotalInputTokens += chatResponse.InputTokens;
                result.TotalOutputTokens += chatResponse.OutputTokens;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LLM call failed at ReAct iteration {Iteration}", iteration + 1);
                result.Answer = $"Error communicating with LLM: {ex.Message}";
                await onProgress(new AgentEvent { Type = AgentEventType.Error, Summary = result.Answer });
                return result;
            }

            // Add assistant's response to history
            messages.Add(new AssistantChatMessage(llmResponse));

            // Parse tool calls from the text output
            var toolCalls = ReActParser.ParseToolCalls(llmResponse);

            if (toolCalls.Count == 0)
            {
                // No tool calls — LLM is giving the final answer
                // Strip any residual tool_call tags just in case
                result.Answer = llmResponse;
                _logger.LogInformation("ReAct agent finished after {Iterations} iterations, {ToolCalls} tool calls", iteration + 1, result.TotalToolCalls);
                break;
            }

            // Execute each parsed tool call
            var toolResults = new List<(string ToolName, string Result)>();

            foreach (var toolCall in toolCalls)
            {
                var argsSummary = FormatToolCallSummary(toolCall.FunctionName, toolCall.Arguments, rootPath);

                // Emit tool start event
                await onProgress(new AgentEvent
                {
                    Type = AgentEventType.ToolCallStart,
                    ToolName = toolCall.FunctionName,
                    ToolArgs = toolCall.Arguments,
                    Summary = argsSummary,
                    Iteration = iteration + 1
                });

                DateTime startTime = DateTime.Now;
                string toolResult;

                var tool = _toolRegistry.GetTool(toolCall.FunctionName);
                if (tool == null)
                {
                    toolResult = $"Error: Unknown tool '{toolCall.FunctionName}'. Available tools: {string.Join(", ", _toolRegistry.GetAllTools().Select(t => t.Name))}";
                }
                else
                {
                    try
                    {
                        toolResult = await tool.ExecuteAsync(toolCall.Arguments, toolContext);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Tool {Tool} execution failed in ReAct loop", toolCall.FunctionName);
                        toolResult = $"Error executing {toolCall.FunctionName}: {ex.Message}";
                    }
                }

                result.TotalToolCalls++;
                var record = new ToolCallRecord
                {
                    ToolName = toolCall.FunctionName,
                    Arguments = toolCall.Arguments,
                    ResultSummary = toolResult.Length > 500
                        ? toolResult[..500] + $"... ({toolResult.Length} chars total)"
                        : toolResult,
                    DurationMs = (DateTime.Now - startTime).Milliseconds,
                };
                result.ToolCalls.Add(record);

                // Track files accessed

                ExtractRelevantFiles(toolCall.FunctionName, toolCall.Arguments, toolResult, rootPath, filesAccessed);

                toolResults.Add((toolCall.FunctionName, toolResult));

                // Emit tool end event
                var resultSummary = FormatToolResultSummary(toolCall.FunctionName, toolResult);
                var resultDetails = toolResult.Length > 5000
                    ? toolResult[..5000] + "\n... (truncated)"
                    : toolResult;
                var (detailLabel, detailItems) = ExtractToolDetailItems(toolCall.FunctionName, toolResult, rootPath);

                await onProgress(new AgentEvent
                {
                    Type = AgentEventType.ToolCallEnd,
                    ToolName = toolCall.FunctionName,
                    ToolArgs = toolCall.Arguments,
                    Summary = argsSummary,
                    DurationMs = (DateTime.Now - startTime).Milliseconds,
                    TotalToolCalls = result.TotalToolCalls,
                    ResultSummary = resultSummary,
                    ResultDetails = resultDetails,
                    DetailLabel = detailLabel,
                    DetailItems = detailItems
                });

                _logger.LogInformation("ReAct tool {Tool} completed in {Ms}ms, result: {Length} chars",
                                       toolCall.FunctionName,
                                       (DateTime.Now - startTime).Milliseconds,
                                       toolResult.Length);
            }

            // Feed tool results back to the LLM as a user message
            var formattedResults = ReActParser.FormatToolResults(toolResults);
            messages.Add(new UserChatMessage(formattedResults));
        }

        if (string.IsNullOrEmpty(result.Answer))
        {
            result.Answer = "I was unable to complete my analysis within the allowed number of iterations. " +
                            "Please try asking a more specific question.";
        }

        result.RelevantFiles = filesAccessed.ToList();
        return result;
    }
}

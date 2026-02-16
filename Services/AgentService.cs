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

    private const int _maxIterations = 30;

    private const string _agentSystemPrompt = @"
You are an expert code analyst with access to tools for exploring a codebase.
Your task is to answer user questions about the code by using the available tools.

A project overview (directory structure and metadata) is automatically provided with the user's question.
Use this overview to plan your exploration — do NOT call list_directory on the project root.

## Strategy
1. Review the project overview already provided — it includes directory structure and key metadata.
2. Use `get_file_outline` to understand a file's structure before reading it (much more efficient than reading the whole file).
3. Use `grep_search` to find relevant code by searching for keywords, class names, function names, or patterns.
4. Use `find_definition` to locate where a class, method, interface, or type is defined.
5. Use `get_related_files` to discover a file's dependencies and dependents.
6. Use `glob_search` to find files by name pattern (faster when you know the filename pattern).
7. Use `read_file` to read specific sections of files — use offset and max_lines for efficiency.
8. Use `list_directory` only if you need to explore a subdirectory not shown in the overview.
9. If your initial search doesn't find what you need, try different keywords, patterns, or file filters.

## Rules
- Be thorough: search with multiple keywords and patterns if the first search doesn't fully answer the question.
- Be precise: cite specific file paths and line numbers when relevant.
- Use markdown formatting for code snippets and file references.
- If you cannot find the answer after thorough searching, say so honestly rather than guessing.
- Respond in the same language as the user's question.
- Focus on the code that exists — don't make assumptions about code you haven't read.
- When analyzing code flow, trace through function calls and class relationships.
- Prefer get_file_outline over read_file when you only need to understand a file's structure.
- Prefer find_definition over grep_search when looking for where something is defined.
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
                                      string? modelProvider = null)
    {
        return RunAsync(question, rootPath, _ => Task.CompletedTask, sessionId, modelProvider);
    }

    /// <summary>
    /// Run with progress callback for SSE streaming
    /// </summary>
    public async Task<AgentResult> RunAsync(string question,
                                            string rootPath,
                                            Func<AgentEvent, Task> onProgress,
                                            string? sessionId = null,
                                            string? modelProvider = null)
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
            new SystemChatMessage(_agentSystemPrompt),
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
                if (toolCall.FunctionName == "read_file")
                {
                    try
                    {
                        var args = JsonSerializer.Deserialize<JsonElement>(toolCall.Arguments);
                        if (args.TryGetProperty("file_path", out var fp))
                        {
                            var filePath = fp.GetString() ?? "";
                            if (!Path.IsPathRooted(filePath))
                            {
                                filePath = Path.GetFullPath(Path.Combine(rootPath, filePath));
                            }

                            filesAccessed.Add(Path.GetRelativePath(rootPath, filePath));
                        }
                    }
                    catch { /* ignore */ }
                }

                messages.Add(new ToolChatMessage(toolCall.CallId, toolResult));

                // Emit tool end event
                await onProgress(new AgentEvent
                {
                    Type = AgentEventType.ToolCallEnd,
                    ToolName = toolCall.FunctionName,
                    Summary = argsSummary,
                    DurationMs = (DateTime.Now - startTime).Milliseconds,
                    TotalToolCalls = result.TotalToolCalls
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
                "grep_search" => $"pattern={args.GetProperty("pattern").GetString()}" + (args.TryGetProperty("include", out var gi) ? $"  include={gi.GetString()}" : ""),

                "glob_search" => $"pattern={args.GetProperty("pattern").GetString()}",

                "read_file" => ExtractFileName(args.GetProperty("file_path").GetString() ?? "", rootPath),

                "list_directory" => args.TryGetProperty("path", out var lp) && lp.GetString() is string p && p.Length > 0
                        ? ExtractFileName(p, rootPath)
                        : "(project root)",

                _ => argsJson.Length > 100 ? argsJson[..100] + "..." : argsJson
            };
        }
        catch
        {
            return argsJson.Length > 100 ? argsJson[..100] + "..." : argsJson;
        }
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
                llmResponse = await provider.ChatAsync(messages);
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
                if (toolCall.FunctionName == "read_file")
                {
                    try
                    {
                        var args = JsonSerializer.Deserialize<JsonElement>(toolCall.Arguments);
                        if (args.TryGetProperty("file_path", out var fp))
                        {
                            var filePath = fp.GetString() ?? "";
                            if (!Path.IsPathRooted(filePath))
                            {
                                filePath = Path.GetFullPath(Path.Combine(rootPath, filePath));
                            }

                            filesAccessed.Add(Path.GetRelativePath(rootPath, filePath));
                        }
                    }
                    catch { /* ignore */ }
                }

                toolResults.Add((toolCall.FunctionName, toolResult));

                // Emit tool end event
                await onProgress(new AgentEvent
                {
                    Type = AgentEventType.ToolCallEnd,
                    ToolName = toolCall.FunctionName,
                    Summary = argsSummary,
                    DurationMs = (DateTime.Now - startTime).Milliseconds,
                    TotalToolCalls = result.TotalToolCalls
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

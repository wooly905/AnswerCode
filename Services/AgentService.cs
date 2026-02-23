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

    public AgentService(ILogger<AgentService> logger, ILLMServiceFactory llmFactory, ToolRegistry toolRegistry)
    {
        _logger = logger;
        _llmFactory = llmFactory;
        _toolRegistry = toolRegistry;
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
        int emptyAssistantResponses = 0;

        var projectOverview = ProjectOverviewBuilder.Build(rootPath);
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
                var textContent = response.TextContent ?? "";
                if (string.IsNullOrWhiteSpace(textContent))
                {
                    emptyAssistantResponses++;
                    _logger.LogWarning("Assistant returned empty content with no tool call at iteration {Iteration}; retrying with enforced tool-use reminder", iteration + 1);

                    if (emptyAssistantResponses >= 3)
                    {
                        result.Answer = "The model repeatedly returned empty responses without any tool calls, so the analysis could not be completed. Please try again later or use a different model.";
                        await onProgress(new AgentEvent { Type = AgentEventType.Error, Summary = result.Answer });
                        break;
                    }

                    messages.Add(new UserChatMessage("You returned no content and did not call any tool. You MUST call at least one tool now and continue the analysis."));
                    continue;
                }

                result.Answer = textContent;
                _logger.LogInformation("Agent finished after {Iterations} iterations, {ToolCalls} tool calls", iteration + 1, result.TotalToolCalls);
                break;
            }

            emptyAssistantResponses = 0;

            foreach (var toolCall in response.ToolCalls)
            {
                // Parse arguments for a clean display summary
                var argsSummary = ToolResultFormatter.FormatToolCallSummary(toolCall.FunctionName, toolCall.Arguments, rootPath);

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
                ToolResultFormatter.ExtractRelevantFiles(toolCall.FunctionName, toolCall.Arguments, toolResult, rootPath, filesAccessed);

                messages.Add(new ToolChatMessage(toolCall.CallId, toolResult));

                // Emit tool end event
                var resultSummary = ToolResultFormatter.FormatToolResultSummary(toolCall.FunctionName, toolResult);
                var resultDetails = toolResult.Length > 5000
                    ? toolResult[..5000] + "\n... (truncated)"
                    : toolResult;
                var (detailLabel, detailItems) = ToolResultFormatter.ExtractToolDetailItems(toolCall.FunctionName, toolResult);

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
        int emptyAssistantResponses = 0;

        // Build system prompt with tool descriptions embedded in text
        var toolDescriptions = _toolRegistry.GetReActToolDescriptions();
        var systemPrompt = ReActParser.BuildReActSystemPrompt(toolDescriptions);

        var projectOverview = ProjectOverviewBuilder.Build(rootPath);
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
                if (string.IsNullOrWhiteSpace(llmResponse))
                {
                    emptyAssistantResponses++;
                    _logger.LogWarning("ReAct assistant returned empty content with no tool calls at iteration {Iteration}; retrying", iteration + 1);

                    if (emptyAssistantResponses >= 3)
                    {
                        result.Answer = "The model repeatedly returned empty responses without any tool calls, so the analysis could not be completed. Please try again later or use a different model.";
                        await onProgress(new AgentEvent { Type = AgentEventType.Error, Summary = result.Answer });
                        break;
                    }

                    messages.Add(new UserChatMessage("You returned no content and did not call any tool. You MUST output at least one <tool_call> and continue the analysis."));
                    continue;
                }

                emptyAssistantResponses = 0;
                // No tool calls — LLM is giving the final answer
                // Strip any residual tool_call tags just in case
                result.Answer = llmResponse;
                _logger.LogInformation("ReAct agent finished after {Iterations} iterations, {ToolCalls} tool calls", iteration + 1, result.TotalToolCalls);
                break;
            }

            emptyAssistantResponses = 0;

            // Execute each parsed tool call
            var toolResults = new List<(string ToolName, string Result)>();

            foreach (var toolCall in toolCalls)
            {
                var argsSummary = ToolResultFormatter.FormatToolCallSummary(toolCall.FunctionName, toolCall.Arguments, rootPath);

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
                ToolResultFormatter.ExtractRelevantFiles(toolCall.FunctionName, toolCall.Arguments, toolResult, rootPath, filesAccessed);

                toolResults.Add((toolCall.FunctionName, toolResult));

                // Emit tool end event
                var resultSummary = ToolResultFormatter.FormatToolResultSummary(toolCall.FunctionName, toolResult);
                var resultDetails = toolResult.Length > 5000
                    ? toolResult[..5000] + "\n... (truncated)"
                    : toolResult;
                var (detailLabel, detailItems) = ToolResultFormatter.ExtractToolDetailItems(toolCall.FunctionName, toolResult);

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

using AnswerCode.Models;
using AnswerCode.Services.Providers;
using AnswerCode.Services.Tools;
using OpenAI.Chat;

namespace AnswerCode.Services;

/// <summary>
/// Agent Service — runs an agentic tool-calling loop where the LLM autonomously decides
/// which tools to use, when to search, and when to stop.
///
/// When conversation history exists, uses a SubAgent architecture to save tokens:
///   Phase 1: Resolve follow-up question into standalone question (with history, 1 LLM call)
///   Phase 2: SubAgent tool loop — full agentic research (without history, N LLM calls)
///   Phase 3: Synthesize final answer (with history + research findings, 1 LLM call)
/// </summary>
public class AgentService(ILogger<AgentService> logger, ILLMServiceFactory llmFactory, ToolRegistry toolRegistry, IConversationHistoryService conversationHistoryService) : IAgentService
{
    private const int _maxIterations = 50;

    /// <summary>Hard cap: refuse to send history beyond this token estimate.</summary>
    private const int _maxHistoryTokens = 200_000;
    /// <summary>Soft cap: trigger compression when estimated tokens reach this level.</summary>
    private const int _compressAtTokens = 180_000;
    /// <summary>Fraction of recent turns to keep verbatim during compression (the rest gets summarized).</summary>
    private const double _keepRecentFraction = 0.2;

    #region System Prompts

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
  - Use `read_symbol` when you need one exact class, method, constructor, property, or field instead of reading a whole file.

- **Understanding Structure**:
  - Use `get_related_files` to understand dependencies (imports/exports) before refactoring or deep analysis.
  - Use `find_definition` to jump to where a symbol is defined.
  - Use `find_references` to see where a symbol is called or used.
  - Use `find_tests` to locate tests that exercise a symbol or file.
  - Use `call_graph` to trace what a method calls (downstream) or what calls it (upstream) — ideal for understanding execution flow and impact of changes.

- **External Information**:
  - Use `web_search` when the question requires knowledge beyond the codebase — e.g., library docs, API references, best practices, error explanations, or latest updates.
  - Do NOT use `web_search` for questions answerable by reading the code. Always search the codebase first.

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
- **Finding Files**:
    - Use `glob_search` when you have a rough idea of the filename or area name.
    - Use `grep_search` when you need to find business concepts, keywords, or behavior described in code or comments.
    - Use `list_directory` ONLY when a directory in the overview is truncated or when you need to inspect one specific area more closely.

- **Reading Logic**:
    - Use `get_file_outline` first for large files to understand the major sections before reading details.
    - Use `read_file` to inspect the surrounding workflow when you need business context from multiple statements.
    - Use `read_symbol` when you need one exact class, method, constructor, property, or field without reading the entire file.

- **Tracing Behavior**:
    - Use `find_definition` to locate where an important concept starts.
    - Use `find_references` to understand where a capability is used across the product flow.
    - Use `get_related_files` to understand nearby modules, imports, and dependencies.
    - Use `find_tests` to discover expected behavior, business rules, and covered scenarios.

- **External Information**:
    - Use `web_search` when the question needs context beyond the codebase — e.g., what a library does, industry best practices, or competitive landscape.
    - Always search the codebase first before reaching for external information.

## Exploration Strategy
1. Identify the high-level feature areas related to the question (e.g., user login, order processing).
2. Search for files and code related to those areas.
3. Read and understand the logic, then describe it in business terms.

## Handling Truncated Results
If a tool result is truncated (for example, shows ""... more matches"" or the overview shows ""... and X more files""):
- Treat that as a signal that more relevant information exists.
- Narrow the search or inspect the specific directory.
- Do NOT assume the hidden results are unimportant.

## Final Answer Format
- Use plain language a non-developer can understand.
- Structure the answer with clear sections: **Overview**, **Business Workflow**, **Module Interactions**, **Key Rules / Business Constraints**.
- Do NOT include file paths, line numbers, class names, or method names in the final answer.
- If no relevant logic was found after a thorough search, say so clearly.
- Respond in the same language as the user's question.
";

    #endregion

    #region SubAgent Prompts

    private const string _contextResolutionPrompt = @"
You are a context resolver. Given a conversation history and the user's latest question, rewrite the question as a fully self-contained question that can be understood without the conversation history.

Rules:
- Preserve ALL relevant context from the history that is needed to understand the question.
- Include specific technical terms, file names, class names, or concepts from the history that the question refers to.
- Output ONLY the rewritten question — no preamble, no explanation.
- If the question is already self-contained, output it unchanged.
- Respond in the same language as the user's question.
";

    private const string _developerSynthesisPrompt = @"
You are an expert code analyst synthesizing a final answer for a follow-up question in an ongoing conversation.

Below you will find:
1. The conversation history (previous Q&A turns)
2. The user's current question
3. Research findings from analyzing the codebase

Instructions:
- Use the research findings as your primary source of truth.
- Reference the conversation context naturally where it adds clarity.
- Cite specific file paths and line numbers from the findings.
- Do not repeat information already well-covered in earlier turns unless it provides new value.
- Respond in the same language as the user's question.
";

    private const string _pmSynthesisPrompt = @"
You are a knowledgeable business analyst synthesizing a final answer for a follow-up question in an ongoing conversation with a Program Manager.

Below you will find:
1. The conversation history (previous Q&A turns)
2. The user's current question
3. Research findings from analyzing the codebase

Instructions:
- Use the research findings as your primary source of truth.
- Translate technical findings into plain, non-technical language.
- Describe things in terms of business workflows and user-facing behavior.
- Do NOT include code snippets, file paths, or class names.
- Reference the conversation context naturally where it adds clarity.
- Respond in the same language as the user's question.
";

    private const string _compressionPrompt = @"
You are a conversation compressor. Summarize the following conversation history into a concise but information-rich summary.

Rules:
- Preserve ALL important facts, conclusions, and decisions from the conversation.
- Preserve specific technical details: file paths, class/method names, line numbers, configuration keys, and architecture decisions.
- Preserve the chronological flow of topics discussed.
- Remove redundant back-and-forth, pleasantries, and verbose explanations — keep only the substance.
- Structure the summary with clear bullet points or short paragraphs grouped by topic.
- Respond in the same language as the conversation.
- Output ONLY the summary — no preamble like 'Here is the summary'.
";

    #endregion

    /// <summary>
    /// Run without progress callback (original API)
    /// </summary>
    public Task<AgentResult> RunAsync(string question,
                                      string rootPath,
                                      string? sessionId = null,
                                      string? modelProvider = null,
                                      string? userRole = null,
                                      List<ConversationTurn>? conversationHistory = null)
    {
        return RunAsync(question, rootPath, _ => Task.CompletedTask, sessionId, modelProvider, userRole, conversationHistory);
    }

    /// <summary>
    /// Main orchestrator — uses SubAgent architecture when conversation history exists.
    /// When no history, runs the tool loop directly with zero overhead.
    /// </summary>
    public async Task<AgentResult> RunAsync(string question,
                                            string rootPath,
                                            Func<AgentEvent, Task> onProgress,
                                            string? sessionId = null,
                                            string? modelProvider = null,
                                            string? userRole = null,
                                            List<ConversationTurn>? conversationHistory = null)
    {
        logger.LogInformation("Agent starting for question: {Question}, project: {RootPath}", question, rootPath);

        var provider = llmFactory.GetProvider(modelProvider);
        await onProgress(new AgentEvent { Type = AgentEventType.Started });

        var projectOverview = ProjectOverviewBuilder.Build(rootPath);
        bool hasHistory = conversationHistory is { Count: > 0 };

        if (!hasHistory)
        {
            // No history — run tool loop directly (zero overhead, same behavior as before)
            if (!provider.SupportsToolCalling)
            {
                logger.LogInformation("Provider {Provider} does not support native tool calling, using ReAct agent loop", provider.Name);
                return await RunReActToolLoopAsync(question, rootPath, provider, onProgress, projectOverview);
            }
            return await RunNativeToolLoopAsync(question, rootPath, provider, onProgress, userRole, projectOverview);
        }

        // === SubAgent architecture: 3 phases ===

        // Phase 0: Check history token budget and compress if needed
        int estimatedTokens = EstimateHistoryTokens(conversationHistory!);
        logger.LogInformation("Estimated history tokens: {Tokens} ({Turns} turns)", estimatedTokens, conversationHistory!.Count);

        if (estimatedTokens >= _compressAtTokens)
        {
            logger.LogInformation("History tokens ({Tokens}) exceed compression threshold ({Threshold}), compressing...", estimatedTokens, _compressAtTokens);
            try
            {
                conversationHistory = await CompressHistoryAsync(provider, conversationHistory);

                // Persist compressed history back to the store
                if (sessionId != null)
                    conversationHistoryService.ReplaceTurns(sessionId, conversationHistory);

                int newEstimate = EstimateHistoryTokens(conversationHistory);
                logger.LogInformation("History compressed: {OldTokens} -> {NewTokens} tokens, {Turns} turns", estimatedTokens, newEstimate, conversationHistory.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "History compression failed, proceeding with uncompressed history");
            }
        }

        // Phase 1: Resolve follow-up question into standalone question
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseStart, Phase = 1, PhaseLabel = "Context Resolution" });
        logger.LogInformation("SubAgent Phase 1: Resolving follow-up question with {Turns} history turns", conversationHistory.Count);
        string resolvedQuestion;
        int p1InputTokens = 0, p1OutputTokens = 0;
        try
        {
            (resolvedQuestion, p1InputTokens, p1OutputTokens) = await ResolveQuestionAsync(
                provider, question, conversationHistory);
            logger.LogInformation("SubAgent Phase 1 complete. Resolved: {Resolved}", resolvedQuestion);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Phase 1 (context resolution) failed, falling back to original question");
            resolvedQuestion = question;
        }
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseEnd, Phase = 1, PhaseLabel = "Context Resolution", ResolvedQuestion = resolvedQuestion });

        // Phase 2: SubAgent tool loop (no history — the core token saving)
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseStart, Phase = 2, PhaseLabel = "SubAgent Research" });
        logger.LogInformation("SubAgent Phase 2: Running tool loop with resolved question (no history)");
        AgentResult result;
        if (!provider.SupportsToolCalling)
        {
            logger.LogInformation("Provider {Provider} does not support native tool calling, using ReAct agent loop", provider.Name);
            result = await RunReActToolLoopAsync(resolvedQuestion, rootPath, provider, onProgress, projectOverview);
        }
        else
        {
            result = await RunNativeToolLoopAsync(resolvedQuestion, rootPath, provider, onProgress, userRole, projectOverview);
        }
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseEnd, Phase = 2, PhaseLabel = "SubAgent Research", Summary = $"{result.TotalToolCalls} tool calls, {result.IterationCount} iterations" });

        // Phase 3: Synthesize final answer with conversation context + research findings
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseStart, Phase = 3, PhaseLabel = "Answer Synthesis" });
        logger.LogInformation("SubAgent Phase 3: Synthesizing answer with conversation context");
        int p3InputTokens = 0, p3OutputTokens = 0;
        try
        {
            var (finalAnswer, p3In, p3Out) = await SynthesizeAnswerAsync(
                provider, question, conversationHistory, result.Answer, userRole);
            result.Answer = finalAnswer;
            p3InputTokens = p3In;
            p3OutputTokens = p3Out;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Phase 3 (synthesis) failed, using SubAgent answer directly");
        }
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseEnd, Phase = 3, PhaseLabel = "Answer Synthesis" });

        // Track main agent tokens (Phase 1 + Phase 3) separately from subagent (Phase 2)
        result.MainAgentInputTokens = p1InputTokens + p3InputTokens;
        result.MainAgentOutputTokens = p1OutputTokens + p3OutputTokens;
        result.TotalInputTokens += result.MainAgentInputTokens;
        result.TotalOutputTokens += result.MainAgentOutputTokens;

        return result;
    }

    #region SubAgent Phases

    /// <summary>
    /// Inject conversation history turns into a message list, handling summary turns specially.
    /// </summary>
    private static void InjectHistoryMessages(List<ChatMessage> messages, List<ConversationTurn> history)
    {
        foreach (var turn in history)
        {
            if (turn.IsSummary)
            {
                messages.Add(new UserChatMessage($"[Summary of earlier conversation]\n{turn.Content}"));
            }
            else if (turn.Role == "user")
            {
                messages.Add(new UserChatMessage(turn.Content));
            }
            else
            {
                messages.Add(new AssistantChatMessage(turn.Content));
            }
        }
    }

    /// <summary>
    /// Phase 1: Resolve a follow-up question into a self-contained question using conversation history.
    /// This allows the SubAgent (Phase 2) to run without carrying conversation history.
    /// </summary>
    private async Task<(string resolvedQuestion, int inputTokens, int outputTokens)> ResolveQuestionAsync(
        ILLMProvider provider, string question, List<ConversationTurn> history)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(_contextResolutionPrompt)
        };

        InjectHistoryMessages(messages, history);
        messages.Add(new UserChatMessage($"## Current Question (rewrite this as self-contained)\n{question}"));

        var response = await provider.ChatAsync(messages);
        var resolved = response.TextContent?.Trim() ?? question;
        return (resolved, response.InputTokens, response.OutputTokens);
    }

    /// <summary>
    /// Phase 3: Synthesize a final answer using conversation history and SubAgent research findings.
    /// </summary>
    private async Task<(string answer, int inputTokens, int outputTokens)> SynthesizeAnswerAsync(
        ILLMProvider provider, string question, List<ConversationTurn> history,
        string findings, string? userRole)
    {
        var systemPrompt = string.Equals(userRole, "PM", StringComparison.OrdinalIgnoreCase)
            ? _pmSynthesisPrompt
            : _developerSynthesisPrompt;

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

        InjectHistoryMessages(messages, history);
        messages.Add(new UserChatMessage($"## Current Question\n{question}\n\n## Research Findings\n{findings}"));

        var response = await provider.ChatAsync(messages);
        return (response.TextContent ?? findings, response.InputTokens, response.OutputTokens);
    }

    /// <summary>
    /// Estimate the token count of conversation history using a conservative character-based heuristic.
    /// Mixed English/CJK content averages roughly 1 token per 3 characters.
    /// </summary>
    private static int EstimateHistoryTokens(List<ConversationTurn> history)
    {
        long totalChars = 0;
        foreach (var turn in history)
            totalChars += turn.Content.Length;
        return (int)(totalChars / 3);
    }

    /// <summary>
    /// Compress conversation history by summarizing older turns via LLM.
    /// Keeps the most recent turns verbatim and replaces older turns with a condensed summary.
    /// </summary>
    private async Task<List<ConversationTurn>> CompressHistoryAsync(
        ILLMProvider provider, List<ConversationTurn> history)
    {
        // Determine split point: keep at least the last 2 turns (1 Q&A pair)
        int keepCount = Math.Max(2, (int)(history.Count * _keepRecentFraction));
        // Align to pair boundary (keep even number of turns from the end)
        if (keepCount % 2 != 0)
            keepCount++;
        keepCount = Math.Min(keepCount, history.Count);

        int compressCount = history.Count - keepCount;
        if (compressCount <= 0)
        {
            logger.LogInformation("Not enough turns to compress ({Count} turns, keeping {Keep})", history.Count, keepCount);
            return history;
        }

        var turnsToCompress = history.GetRange(0, compressCount);
        var turnsToKeep = history.GetRange(compressCount, keepCount);

        // Build the conversation text for summarization
        var conversationText = new System.Text.StringBuilder();
        foreach (var turn in turnsToCompress)
        {
            var label = turn.IsSummary ? "Summary" : (turn.Role == "user" ? "User" : "Assistant");
            conversationText.AppendLine($"**{label}:** {turn.Content}");
            conversationText.AppendLine();
        }

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(_compressionPrompt),
            new UserChatMessage(conversationText.ToString())
        };

        var response = await provider.ChatAsync(messages);
        var summary = response.TextContent?.Trim();

        if (string.IsNullOrWhiteSpace(summary))
        {
            logger.LogWarning("Compression returned empty summary, keeping original history");
            return history;
        }

        logger.LogInformation("Compressed {CompressCount} turns into summary ({SummaryLength} chars), keeping {KeepCount} recent turns",
            compressCount, summary.Length, keepCount);

        // Build new history: [summary] + [recent turns]
        var compressed = new List<ConversationTurn>
        {
            new() { Role = "assistant", Content = summary, IsSummary = true }
        };
        compressed.AddRange(turnsToKeep);
        return compressed;
    }

    #endregion

    #region Tool Loops (Phase 2)

    /// <summary>
    /// Native tool-calling loop — the LLM uses function calling to invoke tools.
    /// Runs WITHOUT conversation history (SubAgent context).
    /// </summary>
    private async Task<AgentResult> RunNativeToolLoopAsync(
        string question, string rootPath, ILLMProvider provider,
        Func<AgentEvent, Task> onProgress, string? userRole, string projectOverview)
    {
        var toolContext = new ToolContext
        {
            RootPath = rootPath,
            Logger = logger
        };

        var result = new AgentResult();
        var chatTools = toolRegistry.GetChatToolDefinitions();
        var filesAccessed = new HashSet<string>();
        int emptyAssistantResponses = 0;

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(string.Equals(userRole, "PM", StringComparison.OrdinalIgnoreCase)
                ? _pmSystemPrompt
                : _agentSystemPrompt),
            new UserChatMessage($"## Project Overview\n{projectOverview}"),
            new UserChatMessage($"## Question\n{question}")
        };

        for (int iteration = 0; iteration < _maxIterations; iteration++)
        {
            result.IterationCount = iteration + 1;
            logger.LogInformation("Agent iteration {Iteration}/{Max}", iteration + 1, _maxIterations);

            await onProgress(new AgentEvent
            {
                Type = AgentEventType.SubAgentThinking,
                Iteration = iteration + 1,
                Thinking = iteration == 0
                    ? "Analyzing question and planning approach..."
                    : $"Analyzing results and deciding next step... (iteration {iteration + 1})"
            });

            LLMChatResponse response;
            try
            {
                response = await provider.ChatWithToolsAsync(messages, chatTools);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LLM call failed at iteration {Iteration}", iteration + 1);
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
                    logger.LogWarning("Assistant returned empty content with no tool call at iteration {Iteration}; retrying with enforced tool-use reminder", iteration + 1);

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
                logger.LogInformation("Agent finished after {Iterations} iterations, {ToolCalls} tool calls", iteration + 1, result.TotalToolCalls);
                break;
            }

            emptyAssistantResponses = 0;

            foreach (var toolCall in response.ToolCalls)
            {
                var argsSummary = ToolResultFormatter.FormatToolCallSummary(toolCall.FunctionName, toolCall.Arguments, rootPath);

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

                var tool = toolRegistry.GetTool(toolCall.FunctionName);
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
                        logger.LogError(ex, "Tool {Tool} execution failed", toolCall.FunctionName);
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

                ToolResultFormatter.ExtractRelevantFiles(toolCall.FunctionName, toolCall.Arguments, toolResult, rootPath, filesAccessed);

                messages.Add(new ToolChatMessage(toolCall.CallId, toolResult));

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

                logger.LogInformation("Tool {Tool} completed in {Ms}ms, result: {Length} chars",
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
    /// ReAct tool loop — uses text-based tool calling so any LLM can be an agent,
    /// without requiring native tool calling (function calling) support.
    /// Runs WITHOUT conversation history (SubAgent context).
    /// </summary>
    private async Task<AgentResult> RunReActToolLoopAsync(
        string question, string rootPath, ILLMProvider provider,
        Func<AgentEvent, Task> onProgress, string projectOverview)
    {
        logger.LogInformation("Starting ReAct agent loop for provider {Provider}", provider.Name);

        var toolContext = new ToolContext { RootPath = rootPath, Logger = logger };
        var result = new AgentResult();
        var filesAccessed = new HashSet<string>();
        int emptyAssistantResponses = 0;

        var toolDescriptions = toolRegistry.GetReActToolDescriptions();
        var systemPrompt = ReActParser.BuildReActSystemPrompt(toolDescriptions);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage($"## Project Overview\n{projectOverview}"),
            new UserChatMessage($"## Question\n{question}")
        };

        for (int iteration = 0; iteration < _maxIterations; iteration++)
        {
            result.IterationCount = iteration + 1;
            logger.LogInformation("ReAct iteration {Iteration}/{Max}", iteration + 1, _maxIterations);

            await onProgress(new AgentEvent
            {
                Type = AgentEventType.SubAgentThinking,
                Iteration = iteration + 1,
                Thinking = iteration == 0
                    ? "Analyzing question and planning approach..."
                    : $"Analyzing results and deciding next step... (iteration {iteration + 1})"
            });

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
                logger.LogError(ex, "LLM call failed at ReAct iteration {Iteration}", iteration + 1);
                result.Answer = $"Error communicating with LLM: {ex.Message}";
                await onProgress(new AgentEvent { Type = AgentEventType.Error, Summary = result.Answer });
                return result;
            }

            messages.Add(new AssistantChatMessage(llmResponse));

            var toolCalls = ReActParser.ParseToolCalls(llmResponse);

            if (toolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(llmResponse))
                {
                    emptyAssistantResponses++;
                    logger.LogWarning("ReAct assistant returned empty content with no tool calls at iteration {Iteration}; retrying", iteration + 1);

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
                result.Answer = llmResponse;
                logger.LogInformation("ReAct agent finished after {Iterations} iterations, {ToolCalls} tool calls", iteration + 1, result.TotalToolCalls);
                break;
            }

            emptyAssistantResponses = 0;

            var toolResults = new List<(string ToolName, string Result)>();

            foreach (var toolCall in toolCalls)
            {
                var argsSummary = ToolResultFormatter.FormatToolCallSummary(toolCall.FunctionName, toolCall.Arguments, rootPath);

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

                var tool = toolRegistry.GetTool(toolCall.FunctionName);
                if (tool == null)
                {
                    toolResult = $"Error: Unknown tool '{toolCall.FunctionName}'. Available tools: {string.Join(", ", toolRegistry.GetAllTools().Select(t => t.Name))}";
                }
                else
                {
                    try
                    {
                        toolResult = await tool.ExecuteAsync(toolCall.Arguments, toolContext);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Tool {Tool} execution failed in ReAct loop", toolCall.FunctionName);
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

                ToolResultFormatter.ExtractRelevantFiles(toolCall.FunctionName, toolCall.Arguments, toolResult, rootPath, filesAccessed);

                toolResults.Add((toolCall.FunctionName, toolResult));

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

                logger.LogInformation("ReAct tool {Tool} completed in {Ms}ms, result: {Length} chars",
                                      toolCall.FunctionName,
                                      (DateTime.Now - startTime).Milliseconds,
                                      toolResult.Length);
            }

            var formattedResults = ReActParser.FormatToolResults(toolResults);
            messages.Add(new UserChatMessage(formattedResults));
        }

        if (string.IsNullOrEmpty(result.Answer))
        {
            result.Answer = "I was unable to complete my analysis within the allowed number of iterations. " +
                            "Please try asking a more specific question.";
        }

        result.RelevantFiles = [.. filesAccessed];
        return result;
    }

    #endregion
}

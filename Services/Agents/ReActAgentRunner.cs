using System.Diagnostics;
using AnswerCode.Models;
using AnswerCode.Services.Providers;
using AnswerCode.Services.Tools;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace AnswerCode.Services.Agents;

public sealed class ReActAgentRunner(
    ILogger<ReActAgentRunner> logger,
    ToolRegistry toolRegistry,
    IUserInputService userInputService,
    IOptions<AgentSettings> settingsOptions)
{
    private readonly AgentSettings _settings = settingsOptions.Value;

    public async Task<AgentResult> RunAsync(
        string question,
        string rootPath,
        ILLMProvider provider,
        Func<AgentEvent, Task> onProgress,
        string projectOverview,
        string? sessionId,
        int maxIterations,
        string? prefetchedContext)
    {
        logger.LogInformation("Starting ReAct agent loop for provider {Provider}", provider.Name);
        var context = new ToolContext
        {
            RootPath = rootPath,
            Logger = logger,
            OnProgress = onProgress,
            SessionId = sessionId,
            UserInputService = userInputService
        };
        var result = new AgentResult();
        var filesAccessed = new HashSet<string>();
        int emptyResponses = 0;
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(ReActParser.BuildReActSystemPrompt(toolRegistry.GetReActToolDescriptions())),
            new UserChatMessage($"## Project Overview\n{projectOverview}")
        };
        if (!string.IsNullOrWhiteSpace(prefetchedContext))
        {
            messages.Add(new UserChatMessage($"## Pre-fetched Symbol Context (verified against the codebase; still confirm with tools before relying on it)\n{prefetchedContext}"));
        }

        messages.Add(new UserChatMessage($"## Question\n{question}"));
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            result.IterationCount = iteration + 1;
            await onProgress(new AgentEvent
            {
                Type = AgentEventType.SubAgentThinking,
                Iteration = iteration + 1,
                Thinking = iteration == 0
                    ? "Analyzing question and planning approach..."
                    : $"Analyzing results and deciding next step... (iteration {iteration + 1})"
            }).ConfigureAwait(false);

            string responseText;
            try
            {
                var response = await provider.ChatAsync(messages).ConfigureAwait(false);
                responseText = response.TextContent ?? string.Empty;
                result.TotalInputTokens += response.InputTokens;
                result.TotalOutputTokens += response.OutputTokens;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LLM call failed at ReAct iteration {Iteration}", iteration + 1);
                result.Answer = $"Error communicating with LLM: {ex.Message}";
                await onProgress(new AgentEvent { Type = AgentEventType.Error, Summary = result.Answer }).ConfigureAwait(false);
                return result;
            }

            messages.Add(new AssistantChatMessage(responseText));
            List<ReActToolCall> toolCalls = ReActParser.ParseToolCalls(responseText);
            if (toolCalls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    result.Answer = ReActParser.CleanNativeTokens(responseText);
                    break;
                }

                if (++emptyResponses >= 3)
                {
                    result.Answer = "The model repeatedly returned empty responses without any tool calls, so the analysis could not be completed. Please try again later or use a different model.";
                    await onProgress(new AgentEvent { Type = AgentEventType.Error, Summary = result.Answer }).ConfigureAwait(false);
                    break;
                }

                messages.Add(new UserChatMessage("You returned no content and did not call any tool. You MUST output at least one <tool_call> and continue the analysis."));
                continue;
            }

            emptyResponses = 0;
            string[] toolResults = await ExecuteBatchAsync(
                toolCalls.Select(call => (call.FunctionName, call.Arguments)).ToList(),
                context,
                rootPath,
                iteration + 1,
                result,
                filesAccessed,
                onProgress).ConfigureAwait(false);
            messages.Add(new UserChatMessage(ReActParser.FormatToolResults(
                toolCalls.Select((call, index) => (call.FunctionName, toolResults[index])).ToList())));
        }

        result.Answer = string.IsNullOrEmpty(result.Answer)
            ? "I was unable to complete my analysis within the allowed number of iterations. Please try asking a more specific question."
            : result.Answer;
        result.RelevantFiles = [.. filesAccessed];
        return result;
    }

    private async Task<string[]> ExecuteBatchAsync(
        IReadOnlyList<(string FunctionName, string Arguments)> calls,
        ToolContext context,
        string rootPath,
        int iteration,
        AgentResult result,
        HashSet<string> filesAccessed,
        Func<AgentEvent, Task> onProgress)
    {
        var results = new string[calls.Count];
        var summaries = new string[calls.Count];
        var syncLock = new object();
        for (int index = 0; index < calls.Count; index++)
        {
            summaries[index] = ToolResultFormatter.FormatToolCallSummary(calls[index].FunctionName, calls[index].Arguments, rootPath);
            await onProgress(new AgentEvent
            {
                Type = AgentEventType.ToolCallStart,
                ToolName = calls[index].FunctionName,
                ToolArgs = calls[index].Arguments,
                Summary = summaries[index],
                Iteration = iteration
            }).ConfigureAwait(false);
        }

        async Task RunOneAsync(int index)
        {
            (string functionName, string arguments) = calls[index];
            long started = Stopwatch.GetTimestamp();
            string toolResult;
            ITool? tool = toolRegistry.GetTool(functionName);
            if (tool is null)
            {
                toolResult = $"Error: Unknown tool '{functionName}'. Available tools: {string.Join(", ", toolRegistry.GetAllTools().Select(item => item.Name))}";
            }
            else
            {
                try
                {
                    toolResult = await tool.ExecuteAsync(arguments, context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Tool {Tool} execution failed", functionName);
                    toolResult = $"Error executing {functionName}: {ex.Message}";
                }
            }

            long durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            int totalCalls;
            lock (syncLock)
            {
                result.TotalToolCalls++;
                totalCalls = result.TotalToolCalls;
                result.ToolCalls.Add(new ToolCallRecord
                {
                    ToolName = functionName,
                    Arguments = arguments,
                    ResultSummary = toolResult.Length > 500 ? toolResult[..500] + $"... ({toolResult.Length} chars total)" : toolResult,
                    DurationMs = durationMs
                });
                ToolResultFormatter.ExtractRelevantFiles(functionName, arguments, toolResult, rootPath, filesAccessed);
            }

            var (detailLabel, detailItems) = ToolResultFormatter.ExtractToolDetailItems(functionName, toolResult);
            await onProgress(new AgentEvent
            {
                Type = AgentEventType.ToolCallEnd,
                ToolName = functionName,
                ToolArgs = arguments,
                Summary = summaries[index],
                DurationMs = durationMs,
                TotalToolCalls = totalCalls,
                ResultSummary = ToolResultFormatter.FormatToolResultSummary(functionName, toolResult),
                ResultDetails = toolResult.Length > 5000 ? toolResult[..5000] + "\n... (truncated)" : toolResult,
                DetailLabel = detailLabel,
                DetailItems = detailItems
            }).ConfigureAwait(false);
            results[index] = toolResult;
        }

        var parallel = Enumerable.Range(0, calls.Count)
            .Where(index => !string.Equals(calls[index].FunctionName, AskUserTool.ToolName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sequential = Enumerable.Range(0, calls.Count).Except(parallel).ToList();
        if (_settings.EnableParallelToolExecution && parallel.Count > 1)
        {
            await Task.WhenAll(parallel.Select(RunOneAsync)).ConfigureAwait(false);
        }
        else
        {
            foreach (int index in parallel)
            {
                await RunOneAsync(index).ConfigureAwait(false);
            }
        }

        foreach (int index in sequential)
        {
            await RunOneAsync(index).ConfigureAwait(false);
        }

        return results;
    }
}
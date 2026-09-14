using System.Text;
using AnswerCode.Models;
using AnswerCode.Services.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AnswerCode.Services.AgentFramework;

/// <summary>
/// Builds a HarnessAgent while preserving AnswerCode prompts, tools, and history ownership.
/// </summary>
public sealed class AnswerCodeAgentHarness(
    ILoggerFactory loggerFactory,
    IServiceProvider services)
{
    public async Task<AgentResult> RunAsync(
        IChatClient chatClient,
        string instructions,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AITool> tools,
        int maximumIterations,
        string rootPath,
        Func<AgentEvent, Task> onProgress,
        CancellationToken cancellationToken = default)
    {
        AIAgent agent = Create(chatClient, instructions, tools, maximumIterations);
        AgentSession session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var eventAdapter = new AgentFrameworkEventAdapter();
        var answerParts = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var answerPartOrder = new List<string>();
        var toolCallResponses = new HashSet<string>(StringComparer.Ordinal);
        var result = new AgentResult();
        var filesAccessed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var responseIds = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<ChatMessage> requestMessages = messages;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await foreach (AgentResponseUpdate update in agent
                .RunStreamingAsync(requestMessages, session, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                AgentFrameworkUpdateResult adapted = eventAdapter.Adapt(update, rootPath);
                string responseKey = update.ResponseId ?? update.MessageId ?? $"update:{answerPartOrder.Count}";
                if (!answerParts.TryGetValue(responseKey, out StringBuilder? responseText))
                {
                    responseText = new StringBuilder();
                    answerParts[responseKey] = responseText;
                    answerPartOrder.Add(responseKey);
                }

                if (adapted.Events.Any(agentEvent => agentEvent.Type == AgentEventType.ToolCallStart))
                {
                    toolCallResponses.Add(responseKey);
                    responseText.Clear();
                }
                else if (!toolCallResponses.Contains(responseKey))
                {
                    responseText.Append(adapted.TextDelta);
                }

                result.TotalInputTokens = checked(result.TotalInputTokens + (int)adapted.InputTokens);
                result.TotalOutputTokens = checked(result.TotalOutputTokens + (int)adapted.OutputTokens);

                if (!string.IsNullOrWhiteSpace(update.ResponseId))
                {
                    responseIds.Add(update.ResponseId);
                }

                foreach (AgentEvent agentEvent in adapted.Events)
                {
                    if (agentEvent.Type == AgentEventType.ToolCallEnd)
                    {
                        result.TotalToolCalls = agentEvent.TotalToolCalls ?? result.TotalToolCalls;
                        result.ToolCalls.Add(new ToolCallRecord
                        {
                            ToolName = agentEvent.ToolName ?? "unknown",
                            Arguments = agentEvent.ToolArgs ?? "{}",
                            ResultSummary = agentEvent.ResultSummary ?? string.Empty,
                            DurationMs = agentEvent.DurationMs ?? 0
                        });

                        ToolResultFormatter.ExtractRelevantFiles(
                            agentEvent.ToolName ?? "unknown",
                            agentEvent.ToolArgs ?? "{}",
                            agentEvent.ResultDetails ?? string.Empty,
                            rootPath,
                            filesAccessed);
                    }

                    await onProgress(agentEvent).ConfigureAwait(false);
                }
            }

            if (BuildAnswer().Length > 0 || attempt == 2)
            {
                break;
            }

            string reminder = result.TotalToolCalls == 0
                ? "You returned no final answer and did not call any tool. You MUST call at least one available tool now, inspect the codebase, and then answer the original question."
                : "You completed tool calls but returned no final answer. Use the tool results already in this conversation and answer the original question now.";
            loggerFactory.CreateLogger<AnswerCodeAgentHarness>().LogWarning(
                "Harness returned no visible answer on attempt {Attempt}; retrying in the same session",
                attempt + 1);
            requestMessages = [new ChatMessage(ChatRole.User, reminder)];
        }

        string answer = BuildAnswer();
        result.Answer = answer.Length > 0
            ? answer
            : "I was unable to complete my analysis within the allowed number of iterations. Please try asking a more specific question.";
        result.IterationCount = Math.Max(1, responseIds.Count);
        result.RelevantFiles = [.. filesAccessed];
        return result;

        string BuildAnswer() => string.Concat(answerPartOrder
            .Where(key => !toolCallResponses.Contains(key))
            .Select(key => answerParts[key].ToString()));
    }

    public AIAgent Create(
        IChatClient chatClient,
        string instructions,
        IReadOnlyList<AITool> tools,
        int maximumIterations)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentNullException.ThrowIfNull(tools);

        if (maximumIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumIterations));
        }

        return chatClient.AsHarnessAgent(
            new HarnessAgentOptions
            {
                Name = "AnswerCode",
                Description = "Answers questions by analyzing the selected codebase.",
                HarnessInstructions = string.Empty,
                MaximumIterationsPerRequest = maximumIterations,
                DisableFileMemory = true,
                DisableWebSearch = true,
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                DisableAgentSkillsProvider = true,
                DisableOpenTelemetry = true,
                ChatOptions = new ChatOptions
                {
                    Instructions = instructions,
                    Tools = [.. tools]
                }
            },
            loggerFactory,
            services);
    }
}
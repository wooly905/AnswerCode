using System.Diagnostics;
using System.Text.Json;
using AnswerCode.Models;
using AnswerCode.Services.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AnswerCode.Services.AgentFramework;

public sealed record AgentFrameworkUpdateResult(
    string TextDelta,
    IReadOnlyList<AgentEvent> Events,
    long InputTokens,
    long OutputTokens);

/// <summary>
/// Projects Agent Framework streaming updates onto AnswerCode's existing SSE contract.
/// </summary>
public sealed class AgentFrameworkEventAdapter
{
    private readonly Dictionary<string, PendingToolCall> _pendingCalls = new(StringComparer.Ordinal);
    private int _totalToolCalls;

    public AgentFrameworkUpdateResult Adapt(AgentResponseUpdate update, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(update);

        var text = new System.Text.StringBuilder();
        var events = new List<AgentEvent>();
        long inputTokens = 0;
        long outputTokens = 0;

        foreach (AIContent content in update.Contents)
        {
            switch (content)
            {
                case TextContent textContent:
                    text.Append(textContent.Text);
                    break;

                case TextReasoningContent reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                    events.Add(new AgentEvent
                    {
                        Type = AgentEventType.SubAgentThinking,
                        Thinking = reasoning.Text
                    });
                    break;

                case FunctionCallContent call when !_pendingCalls.ContainsKey(call.CallId):
                    string argumentsJson = JsonSerializer.Serialize(call.Arguments, JsonSerializerOptions.Web);
                    _pendingCalls[call.CallId] = new PendingToolCall(
                        call.Name,
                        argumentsJson,
                        Stopwatch.GetTimestamp());
                    events.Add(new AgentEvent
                    {
                        Type = AgentEventType.ToolCallStart,
                        ToolName = call.Name,
                        ToolArgs = argumentsJson,
                        Summary = ToolResultFormatter.FormatToolCallSummary(call.Name, argumentsJson, rootPath)
                    });
                    break;

                case FunctionResultContent functionResult:
                    events.Add(CreateToolResultEvent(functionResult));
                    break;

                case UsageContent usage:
                    inputTokens += usage.Details.InputTokenCount ?? 0;
                    outputTokens += usage.Details.OutputTokenCount ?? 0;
                    break;

                case ErrorContent error:
                    events.Add(new AgentEvent
                    {
                        Type = AgentEventType.Error,
                        Summary = error.Message
                    });
                    break;
            }
        }

        return new AgentFrameworkUpdateResult(text.ToString(), events, inputTokens, outputTokens);
    }

    private AgentEvent CreateToolResultEvent(FunctionResultContent functionResult)
    {
        _pendingCalls.Remove(functionResult.CallId, out PendingToolCall? pending);

        string toolName = pending?.Name ?? "unknown";
        string argumentsJson = pending?.ArgumentsJson ?? "{}";
        string resultText = functionResult.Exception is not null
            ? $"Error executing {toolName}: {functionResult.Exception.Message}"
            : FormatResult(functionResult.Result);
        long durationMs = pending is null
            ? 0
            : (long)Stopwatch.GetElapsedTime(pending.StartTimestamp).TotalMilliseconds;
        int totalToolCalls = Interlocked.Increment(ref _totalToolCalls);
        var (detailLabel, detailItems) = ToolResultFormatter.ExtractToolDetailItems(toolName, resultText);

        return new AgentEvent
        {
            Type = AgentEventType.ToolCallEnd,
            ToolName = toolName,
            ToolArgs = argumentsJson,
            DurationMs = durationMs,
            TotalToolCalls = totalToolCalls,
            ResultSummary = ToolResultFormatter.FormatToolResultSummary(toolName, resultText),
            ResultDetails = resultText.Length > 5000
                ? resultText[..5000] + "\n... (truncated)"
                : resultText,
            DetailLabel = detailLabel,
            DetailItems = detailItems
        };
    }

    private static string FormatResult(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        _ => JsonSerializer.Serialize(result, JsonSerializerOptions.Web)
    };

    private sealed record PendingToolCall(string Name, string ArgumentsJson, long StartTimestamp);
}
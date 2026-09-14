using AnswerCode.Models;
using AnswerCode.Services.AgentFramework;
using AnswerCode.Services.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AnswerCode.Tests.Services.AgentFramework;

public class AgentFrameworkEventAdapterTests
{
    [Fact]
    public void Adapt_TextReasoningAndUsage_ProjectsExpectedValues()
    {
        var adapter = new AgentFrameworkEventAdapter();
        var update = new AgentResponseUpdate
        {
            Contents =
            [
                new TextContent("answer"),
                new TextReasoningContent("thinking"),
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = 12,
                    OutputTokenCount = 4
                })
            ]
        };

        AgentFrameworkUpdateResult result = adapter.Adapt(update, "C:/repo");

        Assert.Equal("answer", result.TextDelta);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal(4, result.OutputTokens);
        AgentEvent reasoning = Assert.Single(result.Events);
        Assert.Equal(AgentEventType.SubAgentThinking, reasoning.Type);
        Assert.Equal("thinking", reasoning.Thinking);
    }

    [Fact]
    public void Adapt_FunctionCallAndResult_CorrelatesToolEvents()
    {
        var adapter = new AgentFrameworkEventAdapter();
        const string callId = "call-1";

        AgentFrameworkUpdateResult start = adapter.Adapt(
            new AgentResponseUpdate
            {
                Contents =
                [
                    new FunctionCallContent(
                        callId,
                        GrepTool.ToolName,
                        new Dictionary<string, object?> { ["pattern"] = "TODO" })
                ]
            },
            "C:/repo");

        AgentFrameworkUpdateResult end = adapter.Adapt(
            new AgentResponseUpdate
            {
                Contents = [new FunctionResultContent(callId, "No matches found for pattern 'TODO'")]
            },
            "C:/repo");

        AgentEvent startEvent = Assert.Single(start.Events);
        Assert.Equal(AgentEventType.ToolCallStart, startEvent.Type);
        Assert.Equal(GrepTool.ToolName, startEvent.ToolName);
        Assert.Contains("TODO", startEvent.ToolArgs);

        AgentEvent endEvent = Assert.Single(end.Events);
        Assert.Equal(AgentEventType.ToolCallEnd, endEvent.Type);
        Assert.Equal(GrepTool.ToolName, endEvent.ToolName);
        Assert.Equal("no matches", endEvent.ResultSummary);
        Assert.Equal(1, endEvent.TotalToolCalls);
    }
}
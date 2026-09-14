using System.Runtime.CompilerServices;
using AnswerCode.Models;
using AnswerCode.Services;
using AnswerCode.Services.AgentFramework;
using AnswerCode.Services.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;

namespace AnswerCode.Tests.Services.AgentFramework;

public class AnswerCodeAgentHarnessTests
{
    [Fact]
    public async Task RunAsync_InvokesExistingToolAndReturnsFinalAnswer()
    {
        using var chatClient = new ToolCallingChatClient();
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var harness = new AnswerCodeAgentHarness(NullLoggerFactory.Instance, services);
        var tool = new CountingTool();
        IReadOnlyList<AITool> tools = new AnswerCodeToolAdapter().Adapt(
            [tool],
            new ToolContext { RootPath = "C:/repo" });
        var events = new List<AgentEvent>();

        AgentResult result = await harness.RunAsync(
            chatClient,
            "Use the available tool before answering.",
            [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Read the file")],
            tools,
            maximumIterations: 8,
            rootPath: "C:/repo",
            evt =>
            {
                events.Add(evt);
                return Task.CompletedTask;
            });

        Assert.Equal(1, tool.InvocationCount);
        Assert.True(chatClient.ReceivedToolResult);
        Assert.Equal("analysis complete", result.Answer);
        Assert.Equal(1, result.TotalToolCalls);
        Assert.Equal(9, result.TotalInputTokens);
        Assert.Equal(3, result.TotalOutputTokens);
        Assert.Contains(events, evt => evt.Type == AgentEventType.ToolCallStart);
        Assert.Contains(events, evt => evt.Type == AgentEventType.ToolCallEnd);
    }

    [Fact]
    public async Task RunAsync_EmptyFirstResponse_RetriesWithReminder()
    {
        using var chatClient = new ToolCallingChatClient(returnEmptyFirstResponse: true);
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var harness = new AnswerCodeAgentHarness(NullLoggerFactory.Instance, services);
        var tool = new CountingTool();
        IReadOnlyList<AITool> tools = new AnswerCodeToolAdapter().Adapt(
            [tool],
            new ToolContext { RootPath = "C:/repo" });

        AgentResult result = await harness.RunAsync(
            chatClient,
            "Use the available tool before answering.",
            [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "Read the file")],
            tools,
            maximumIterations: 8,
            rootPath: "C:/repo",
            _ => Task.CompletedTask);

        Assert.True(chatClient.ReceivedRetryReminder);
        Assert.Equal(1, tool.InvocationCount);
        Assert.Equal("analysis complete", result.Answer);
    }

    private sealed class ToolCallingChatClient : IChatClient
    {
        private readonly bool _returnEmptyFirstResponse;
        private int _requestCount;

        public ToolCallingChatClient(bool returnEmptyFirstResponse = false)
        {
            _returnEmptyFirstResponse = returnEmptyFirstResponse;
        }

        public bool ReceivedToolResult { get; private set; }

        public bool ReceivedRetryReminder { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _requestCount++;
            ReceivedRetryReminder |= messages.Any(message =>
                message.Role == ChatRole.User
                && message.Text.Contains("did not call any tool", StringComparison.Ordinal));
            ReceivedToolResult = messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .Any();

            if (_returnEmptyFirstResponse && _requestCount == 1)
            {
                yield return new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, []);
                yield break;
            }

            if (!ReceivedToolResult)
            {
                yield return new ChatResponseUpdate(
                    Microsoft.Extensions.AI.ChatRole.Assistant,
                    [
                        new TextContent("I will inspect the file."),
                        new FunctionCallContent(
                            "call-1",
                            "read_test_file",
                            new Dictionary<string, object?> { ["path"] = "README.md" })
                    ])
                {
                    ResponseId = "response-1"
                };
                yield break;
            }

            yield return new ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "analysis complete")
            {
                ResponseId = "response-2"
            };
            yield return new ChatResponseUpdate(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                [
                    new UsageContent(new UsageDetails
                    {
                        InputTokenCount = 9,
                        OutputTokenCount = 3
                    })
                ])
            {
                ResponseId = "response-2"
            };

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class CountingTool : ITool
    {
        public int InvocationCount { get; private set; }

        public string Name => "read_test_file";

        public string Description => "Reads a test file.";

        public ChatTool GetChatToolDefinition() => ChatTool.CreateFunctionTool(
            functionName: Name,
            functionDescription: Description,
            functionParameters: BinaryData.FromString(
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string" }
                  },
                  "required": ["path"]
                }
                """));

        public Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
        {
            InvocationCount++;
            return Task.FromResult("file contents");
        }
    }
}
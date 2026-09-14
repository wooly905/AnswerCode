using System.Text.Json;
using AnswerCode.Services.AgentFramework;
using AnswerCode.Services.Tools;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace AnswerCode.Tests.Services.AgentFramework;

public class AnswerCodeToolAdapterTests
{
    [Fact]
    public async Task Adapt_PreservesMetadataAndInvokesExistingTool()
    {
        var tool = new RecordingTool();
        var context = new ToolContext { RootPath = "C:/repo" };
        var adapter = new AnswerCodeToolAdapter();

        AIFunction function = Assert.IsType<AnswerCodeToolFunction>(Assert.Single(adapter.Adapt([tool], context)));

        Assert.Equal(tool.Name, function.Name);
        Assert.Equal(tool.Description, function.Description);
        Assert.Equal("object", function.JsonSchema.GetProperty("type").GetString());
        Assert.True(function.JsonSchema.GetProperty("properties").TryGetProperty("path", out _));

        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = "README.md"
        });

        Assert.Equal("read:README.md", result);
        Assert.Equal("README.md", JsonDocument.Parse(tool.ArgumentsJson!).RootElement.GetProperty("path").GetString());
        Assert.Same(context, tool.Context);
    }

    [Fact]
    public async Task InvokeAsync_CancelledToken_DoesNotInvokeTool()
    {
        var tool = new RecordingTool();
        var function = new AnswerCodeToolFunction(tool, new ToolContext { RootPath = "C:/repo" });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await function.InvokeAsync(new AIFunctionArguments(), cancellation.Token));

        Assert.Null(tool.ArgumentsJson);
    }

    private sealed class RecordingTool : ITool
    {
        public string Name => "read_test_file";

        public string Description => "Reads a test file.";

        public string? ArgumentsJson { get; private set; }

        public ToolContext? Context { get; private set; }

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
            ArgumentsJson = argumentsJson;
            Context = context;

            string path = JsonDocument.Parse(argumentsJson).RootElement.GetProperty("path").GetString()!;
            return Task.FromResult($"read:{path}");
        }
    }
}
using AnswerCode.Services.Tools;

namespace AnswerCode.Tests.Services.Tools;

public class ReActParserTests
{
    [Fact]
    public void ParseToolCalls_StandardFormat_ReturnsToolCall()
    {
        var response = """
            <tool_call>
            {"name": "grep_search", "arguments": {"pattern": "foo"}}
            </tool_call>
            """;

        var result = ReActParser.ParseToolCalls(response);

        var call = Assert.Single(result);
        Assert.Equal("grep_search", call.FunctionName);
        Assert.Equal("""{"pattern":"foo"}""", call.Arguments.Replace(" ", ""));
    }

    [Fact]
    public void ParseToolCalls_MultipleStandardCalls_ReturnsAll()
    {
        var response = """
            <tool_call>
            {"name": "grep_search", "arguments": {"pattern": "foo"}}
            </tool_call>
            <tool_call>
            {"name": "read_file", "arguments": {"file_path": "a.cs"}}
            </tool_call>
            """;

        var result = ReActParser.ParseToolCalls(response);

        Assert.Equal(2, result.Count);
        Assert.Equal("grep_search", result[0].FunctionName);
        Assert.Equal("read_file", result[1].FunctionName);
    }

    [Fact]
    public void ParseToolCalls_MalformedJson_IsSkipped()
    {
        var response = "<tool_call>\n{not valid json}\n</tool_call>";

        var result = ReActParser.ParseToolCalls(response);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseToolCalls_NoToolCallArguments_DefaultsToEmptyObject()
    {
        var response = """
            <tool_call>
            {"name": "repo_map"}
            </tool_call>
            """;

        var result = ReActParser.ParseToolCalls(response);

        var call = Assert.Single(result);
        Assert.Equal("{}", call.Arguments);
    }

    [Fact]
    public void ParseToolCalls_NativeFormat_MapsToolNameAndArgumentKeys()
    {
        var response = "<|channel|>commentary to=repo_browser.search <|message|>{\"query\": \"TODO\"}<|call|>";

        var result = ReActParser.ParseToolCalls(response);

        var call = Assert.Single(result);
        Assert.Equal("grep_search", call.FunctionName);
        Assert.Contains("\"pattern\":\"TODO\"", call.Arguments.Replace(" ", ""));
    }

    [Fact]
    public void ParseToolCalls_NativeFormat_UnmappedFunctionName_PassesThrough()
    {
        var response = "<|channel|>commentary to=functions.some_unknown_tool <|message|>{\"a\": 1}<|call|>";

        var result = ReActParser.ParseToolCalls(response);

        var call = Assert.Single(result);
        Assert.Equal("some_unknown_tool", call.FunctionName);
    }

    [Fact]
    public void ParseToolCalls_NoToolCalls_ReturnsEmptyList()
    {
        var result = ReActParser.ParseToolCalls("Just a plain final answer, no tools needed.");

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("<tool_call>\n{\"name\": \"grep_search\", \"arguments\": {}}\n</tool_call>", true)]
    [InlineData("<|channel|>commentary to=functions.read_file <|message|>{}<|call|>", true)]
    [InlineData("plain text final answer", false)]
    public void HasToolCalls_DetectsBothFormats(string response, bool expected)
    {
        Assert.Equal(expected, ReActParser.HasToolCalls(response));
    }

    [Fact]
    public void ExtractThinkingText_RemovesStandardToolCallBlock()
    {
        var response = "Let me search.\n<tool_call>\n{\"name\": \"grep_search\", \"arguments\": {}}\n</tool_call>";

        var thinking = ReActParser.ExtractThinkingText(response);

        Assert.Equal("Let me search.", thinking);
    }

    [Fact]
    public void ExtractThinkingText_RemovesNativeTokensAndAssistantWord()
    {
        var response = "<|start|> assistant <|channel|>analysis<|message|>Thinking about it<|end|>";

        var thinking = ReActParser.ExtractThinkingText(response);

        Assert.DoesNotContain("<|", thinking);
        Assert.DoesNotContain("assistant", thinking, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Thinking about it", thinking);
    }

    [Fact]
    public void CleanNativeTokens_NoSpecialTokens_ReturnsOriginalText()
    {
        var text = "plain response with no tokens";

        Assert.Equal(text, ReActParser.CleanNativeTokens(text));
    }

    [Fact]
    public void FormatToolResults_SingleResult_WrapsInSingleTag()
    {
        var results = new List<(string ToolName, string Result)> { ("grep_search", "3 matches") };

        var formatted = ReActParser.FormatToolResults(results);

        Assert.Equal("<tool_result name=\"grep_search\">\n3 matches\n</tool_result>", formatted);
    }

    [Fact]
    public void FormatToolResults_MultipleResults_JoinsWithBlankLine()
    {
        var results = new List<(string ToolName, string Result)>
        {
            ("grep_search", "3 matches"),
            ("read_file", "file contents")
        };

        var formatted = ReActParser.FormatToolResults(results);

        Assert.Contains("<tool_result name=\"grep_search\">\n3 matches\n</tool_result>", formatted);
        Assert.Contains("<tool_result name=\"read_file\">\nfile contents\n</tool_result>", formatted);
        Assert.Contains("\n\n", formatted);
    }
}

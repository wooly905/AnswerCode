using OpenAI.Chat;

namespace AnswerCode.Services.Providers;

/// <summary>
/// Response from a tool-calling LLM chat round
/// </summary>
public class LLMChatResponse
{
    /// <summary>
    /// Whether this response contains tool calls (vs. a final text answer)
    /// </summary>
    public bool IsToolCall { get; init; }

    /// <summary>
    /// Text content (only set when IsToolCall is false)
    /// </summary>
    public string? TextContent { get; init; }

    /// <summary>
    /// Tool calls requested by the model
    /// </summary>
    public IReadOnlyList<LLMToolCallInfo> ToolCalls { get; init; } = [];

    /// <summary>
    /// The ChatMessage representing the assistant's response, to be added back to conversation history.
    /// This preserves tool call metadata required by the API.
    /// </summary>
    public required ChatMessage AssistantMessage { get; init; }
}

/// <summary>
/// Info about a single tool call requested by the LLM
/// </summary>
public class LLMToolCallInfo
{
    public required string CallId { get; init; }
    public required string FunctionName { get; init; }
    public required string Arguments { get; init; }
}

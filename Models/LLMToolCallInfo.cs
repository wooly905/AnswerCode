namespace AnswerCode.Models;

/// <summary>
/// Info about a single tool call requested by the LLM
/// </summary>
public class LLMToolCallInfo
{
    public required string CallId { get; init; }
    public required string FunctionName { get; init; }
    public required string Arguments { get; init; }
}

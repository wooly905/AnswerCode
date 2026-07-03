using System.Text.Json;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Lets the agent pause and ask the human user a clarifying question when facing a genuinely
/// ambiguous or high-impact decision that cannot be safely resolved by reading the codebase
/// (e.g. conflicting requirements, missing business rules, choosing between multiple valid designs).
///
/// The question is emitted to the UI as a <see cref="Models.AgentEventType.UserQuestion"/> SSE event;
/// the client submits the answer via a separate HTTP endpoint, which resolves the pending
/// <see cref="IUserInputService"/> wait and lets the tool return the answer to the LLM.
/// </summary>
public class AskUserTool : ITool
{
    public const string ToolName = "ask_user";

    /// <summary>How long to wait for the user to respond before giving up.</summary>
    private static readonly TimeSpan _answerTimeout = TimeSpan.FromMinutes(5);

    public string Name => ToolName;

    public string Description =>
"""
Ask the human user a direct clarifying question when you face a genuinely ambiguous or
high-impact decision that cannot be safely resolved by reading the codebase — for example:
conflicting requirements, missing business rules, or choosing between multiple reasonable
implementation approaches that would materially change the result.
Do NOT use this for things you can find by reading code, searching, or reasoning yourself.
Use it sparingly — only when guessing wrong would waste significant work or mislead the user.
""";

    public ChatTool GetChatToolDefinition()
    {
        return ChatTool.CreateFunctionTool(
            functionName: Name,
            functionDescription: Description,
            functionParameters: BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    question = new
                    {
                        type = "string",
                        description = "The clarifying question to ask the user. Be specific and concise."
                    },
                    options = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Optional list of suggested answer choices to present to the user."
                    }
                },
                required = new[] { "question" }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        if (context.OnProgress == null || context.UserInputService == null)
        {
            return "Error: asking the user is not supported in this execution context.";
        }

        JsonElement args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        string? question = args.TryGetProperty("question", out var q) ? q.GetString() : null;

        if (string.IsNullOrWhiteSpace(question))
        {
            return "Error: 'question' parameter is required.";
        }

        List<string>? options = null;
        if (args.TryGetProperty("options", out var optsEl) && optsEl.ValueKind == JsonValueKind.Array)
        {
            options = optsEl.EnumerateArray()
                            .Select(e => e.GetString())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s!)
                            .ToList();
        }

        string questionId = Guid.NewGuid().ToString("N");

        await context.OnProgress(new Models.AgentEvent
        {
            Type = Models.AgentEventType.UserQuestion,
            QuestionId = questionId,
            Question = question,
            QuestionOptions = options
        });

        string? answer = await context.UserInputService.WaitForAnswerAsync(questionId, _answerTimeout);

        if (string.IsNullOrWhiteSpace(answer))
        {
            return "The user did not respond within the time limit. Proceed using your best judgment, and clearly state in your final answer which assumption you made.";
        }

        return $"User answered: {answer}";
    }
}

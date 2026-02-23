using System.Text;
using System.Text.Json;
using AnswerCode.Models;
using OpenAI.Chat;

namespace AnswerCode.Services.Providers;

/// <summary>
/// Base class for LLM providers containing shared chat, tool-calling, and parsing logic.
/// Subclasses only need to supply the <see cref="OpenAI.Chat.ChatClient"/> via a static factory method
/// called from the constructor, and set the <see cref="Name"/> property.
/// </summary>
public abstract class BaseLLMProvider(ChatClient chatClient, string systemPromptBase) : ILLMProvider
{
    protected readonly ChatClient ChatClient = chatClient;
    protected readonly string SystemPromptBase = systemPromptBase;

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public virtual bool SupportsToolCalling => true;

    /// <inheritdoc/>
    public virtual async Task<string> AskAsync(string systemPrompt, string userQuestion, string codeContext)
    {
        var messages = BuildMessages(systemPrompt, userQuestion, codeContext);
        var completion = await ChatClient.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            MaxOutputTokenCount = 8000,
            Temperature = 0.3f
        });
        return completion.Value.Content[0].Text;
    }

    /// <inheritdoc/>
    public virtual async Task<List<string>> ExtractKeywordsAsync(string question)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                "Extract the most important search keywords from the user's question about code. " +
                "Return a JSON array of 3-5 keywords/phrases. Return ONLY a JSON array."),
            new UserChatMessage(question)
        };

        var completion = await ChatClient.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            MaxOutputTokenCount = 300,
            Temperature = 0.3f
        });
        return ParseKeywords(completion.Value.Content[0].Text.Trim());
    }

    /// <inheritdoc/>
    public virtual async Task<LLMChatResponse> ChatAsync(IList<ChatMessage> messages)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 8000,
            Temperature = 0.3f
        };

        var completion = await ChatClient.CompleteChatAsync(messages, options);
        var value = completion.Value;
        var text = value.Content.Count > 0 ? value.Content[0].Text ?? "" : "";
        return new LLMChatResponse
        {
            IsToolCall = false,
            TextContent = text,
            AssistantMessage = new AssistantChatMessage(text),
            InputTokens = value.Usage?.InputTokenCount ?? 0,
            OutputTokens = value.Usage?.OutputTokenCount ?? 0
        };
    }

    /// <inheritdoc/>
    public virtual async Task<LLMChatResponse> ChatWithToolsAsync(IList<ChatMessage> messages, IReadOnlyList<ChatTool> tools)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 8000,
            Temperature = 0.3f
        };

        foreach (var tool in tools)
        {
            options.Tools.Add(tool);
        }

        var completion = await ChatClient.CompleteChatAsync(messages, options);
        var value = completion.Value;

        // Only treat as tool call if there are actual tool calls
        if (value.FinishReason == ChatFinishReason.ToolCalls && value.ToolCalls.Count > 0)
        {
            return new LLMChatResponse
            {
                IsToolCall = true,
                ToolCalls = value.ToolCalls.Select(tc => new LLMToolCallInfo
                {
                    CallId = tc.Id,
                    FunctionName = tc.FunctionName,
                    Arguments = tc.FunctionArguments.ToString()
                }).ToList(),
                AssistantMessage = new AssistantChatMessage(value.ToolCalls),
                InputTokens = value.Usage?.InputTokenCount ?? 0,
                OutputTokens = value.Usage?.OutputTokenCount ?? 0
            };
        }

        var text = value.Content.Count > 0 ? value.Content[0].Text : "";
        return new LLMChatResponse
        {
            IsToolCall = false,
            TextContent = text ?? "",
            AssistantMessage = new AssistantChatMessage(text ?? ""),
            InputTokens = value.Usage?.InputTokenCount ?? 0,
            OutputTokens = value.Usage?.OutputTokenCount ?? 0
        };
    }

    /// <summary>
    /// Build the message list for a single-shot ask with system prompt, question, and code context.
    /// </summary>
    protected List<ChatMessage> BuildMessages(string systemPrompt, string userQuestion, string codeContext)
    {
        var fullSystemPrompt = string.IsNullOrEmpty(systemPrompt)
            ? SystemPromptBase
            : $"{SystemPromptBase}\n\n{systemPrompt}";

        var messages = new List<ChatMessage> { new SystemChatMessage(fullSystemPrompt) };

        var userContent = new StringBuilder();
        if (!string.IsNullOrEmpty(codeContext))
        {
            userContent.AppendLine("<code_context>");
            userContent.AppendLine(codeContext);
            userContent.AppendLine("</code_context>\n");
        }
        userContent.AppendLine("<question>");
        userContent.AppendLine(userQuestion);
        userContent.AppendLine("</question>");

        messages.Add(new UserChatMessage(userContent.ToString()));
        return messages;
    }

    /// <summary>
    /// Parse a JSON keyword array from the LLM response, handling optional markdown fences.
    /// </summary>
    protected static List<string> ParseKeywords(string response)
    {
        try
        {
            if (response.StartsWith("```"))
            {
                response = response.Split('\n')
                    .Skip(1)
                    .TakeWhile(l => !l.StartsWith("```"))
                    .Aggregate("", (a, b) => a + b);
            }
            return JsonSerializer.Deserialize<List<string>>(response) ?? [];
        }
        catch { return []; }
    }
}

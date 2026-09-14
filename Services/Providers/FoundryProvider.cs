using System.Text.Json;
using AnswerCode.Models;
using AnswerCode.Services.AgentFramework;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AnswerCode.Services.Providers;

/// <summary>
/// Bridges Foundry text completions into the legacy provider contract used by phases 1 and 3.
/// Native tool calling is owned by AnswerCodeAgentHarness.
/// </summary>
public sealed class FoundryProvider : ILLMProvider
{
    private readonly IChatClient _chatClient;
    private readonly string _systemPromptBase;

    public FoundryProvider(
        string providerKey,
        LLMProviderSettings settings,
        string systemPromptBase,
        AnswerCodeFoundryClient foundryClient)
    {
        Name = providerKey;
        _systemPromptBase = systemPromptBase;
        _chatClient = foundryClient.CreateChatClient(settings);
    }

    public string Name { get; }

    public bool SupportsToolCalling => true;

    public async Task<string> AskAsync(string systemPrompt, string userQuestion, string codeContext)
    {
        string instructions = string.IsNullOrWhiteSpace(systemPrompt)
            ? _systemPromptBase
            : $"{_systemPromptBase}\n\n{systemPrompt}";
        var messages = new List<AIChatMessage>
        {
            new(AIChatRole.System, instructions),
            new(AIChatRole.User, $"<code_context>\n{codeContext}\n</code_context>\n\n{userQuestion}")
        };

        ChatResponse response = await _chatClient.GetResponseAsync(messages);
        return response.Text;
    }

    public async Task<List<string>> ExtractKeywordsAsync(string question)
    {
        var messages = new List<AIChatMessage>
        {
            new(AIChatRole.System,
                "Extract the most important search keywords from the user's question about code. " +
                "Return a JSON array of 3-5 keywords/phrases. Return ONLY a JSON array."),
            new(AIChatRole.User, question)
        };
        ChatResponse response = await _chatClient.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 300 });

        try
        {
            return JsonSerializer.Deserialize<List<string>>(response.Text) ?? [];
        }
        catch (JsonException)
        {
            return response.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
    }

    public async Task<LLMChatResponse> ChatAsync(IList<OpenAI.Chat.ChatMessage> messages)
    {
        List<AIChatMessage> convertedMessages = messages.Select(ConvertMessage).ToList();
        ChatResponse response = await _chatClient.GetResponseAsync(
            convertedMessages,
            new ChatOptions { MaxOutputTokens = 8000 });

        return new LLMChatResponse
        {
            TextContent = response.Text,
            InputTokens = checked((int)(response.Usage?.InputTokenCount ?? 0)),
            OutputTokens = checked((int)(response.Usage?.OutputTokenCount ?? 0))
        };
    }

    private static AIChatMessage ConvertMessage(OpenAI.Chat.ChatMessage message)
    {
        AIChatRole role = message switch
        {
            SystemChatMessage => AIChatRole.System,
            AssistantChatMessage => AIChatRole.Assistant,
            ToolChatMessage => AIChatRole.Tool,
            _ => AIChatRole.User
        };
        string text = string.Concat(message.Content.Select(part => part.Text));
        return new AIChatMessage(role, text);
    }
}
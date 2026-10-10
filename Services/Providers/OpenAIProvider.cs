using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using AnswerCode.Models;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AnswerCode.Services.Providers;

/// <summary>
/// OpenAI-compatible provider using the Responses API (<c>{Endpoint}responses</c>).
/// Also used as the default for any non-Azure provider (e.g., Ollama, local endpoints).
/// </summary>
public class OpenAIProvider : ILLMProvider
{
    private readonly IChatClient _chatClient;
    private readonly string _systemPromptBase;
    private readonly LLMProviderSettings _settings;
    private readonly bool _useReasoningModelParameters;

    public OpenAIProvider(LLMProviderSettings settings,
                          string systemPromptBase,
                          ILogger<OpenAIProvider> logger,
                          string providerKey = ProviderKeys.OpenAI)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Name = providerKey;
        _settings = settings;
        _systemPromptBase = systemPromptBase;
        _useReasoningModelParameters = ShouldUseReasoningModelParameters(settings);
        _chatClient = CreateChatClient(settings, logger, providerKey);
    }

    public string Name { get; }

    public bool SupportsToolCalling => _settings.SupportsToolCalling ?? true;

    public async Task<string> AskAsync(string systemPrompt, string userQuestion, string codeContext)
    {
        var fullSystemPrompt = string.IsNullOrEmpty(systemPrompt)
            ? _systemPromptBase
            : $"{_systemPromptBase}\n\n{systemPrompt}";

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

        var messages = new List<AIChatMessage>
        {
            new(AIChatRole.System, fullSystemPrompt),
            new(AIChatRole.User, userContent.ToString())
        };

        ChatResponse response = await _chatClient.GetResponseAsync(messages, CreateChatOptions(8000));
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

        ChatResponse response = await _chatClient.GetResponseAsync(messages, CreateChatOptions(300));
        return BaseLLMProvider.ParseKeywords(response.Text.Trim());
    }

    public async Task<LLMChatResponse> ChatAsync(IList<OpenAI.Chat.ChatMessage> messages)
    {
        List<AIChatMessage> convertedMessages = messages.Select(ConvertMessage).ToList();
        ChatResponse response = await _chatClient.GetResponseAsync(convertedMessages, CreateChatOptions(8000));
        var (cleanedText, _) = BaseLLMProvider.ExtractThinking(response.Text);

        return new LLMChatResponse
        {
            TextContent = cleanedText,
            InputTokens = checked((int)(response.Usage?.InputTokenCount ?? 0)),
            OutputTokens = checked((int)(response.Usage?.OutputTokenCount ?? 0))
        };
    }

    private ChatOptions CreateChatOptions(int maxOutputTokens)
    {
        var options = new ChatOptions { MaxOutputTokens = maxOutputTokens };
        if (!_useReasoningModelParameters)
        {
            options.Temperature = 0.3f;
        }

        return options;
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

    private static IChatClient CreateChatClient(LLMProviderSettings settings, ILogger<OpenAIProvider> logger, string providerKey)
    {
        var endpoint = settings.Endpoint ?? throw new InvalidOperationException($"{providerKey}:Endpoint not configured");
        var apiKey = settings.ApiKey ?? throw new InvalidOperationException($"{providerKey}:ApiKey not configured");
        var modelName = settings.Model ?? "gpt-oss-120b";

        var openAIClient = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
                Transport = new HttpClientPipelineTransport(new HttpClient(new LLMRequestLoggingHandler(logger)))
            });

#pragma warning disable OPENAI001
        return openAIClient.GetResponsesClient().AsIChatClient(modelName);
#pragma warning restore OPENAI001
    }

    private static bool ShouldUseReasoningModelParameters(LLMProviderSettings settings)
    {
        if (settings.UseReasoningModelParameters.HasValue)
        {
            return settings.UseReasoningModelParameters.Value;
        }

        // "auto" lets the endpoint pick the model, which may be a reasoning model that rejects temperature.
        return string.Equals(settings.Model?.Trim(), "auto", StringComparison.OrdinalIgnoreCase)
            || BaseLLMProvider.IsKnownReasoningChatModel(settings.Model);
    }
}

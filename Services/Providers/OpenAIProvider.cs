using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using AnswerCode.Models;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace AnswerCode.Services.Providers;

public class OpenAIProvider : ILLMProvider
{
    private readonly ChatClient _chatClient;
    private readonly string _modelName;
    private readonly string _systemPromptBase;
    private readonly string _providerName;

    public string Name => _providerName;
    public bool SupportsToolCalling => true;

    public OpenAIProvider(LLMProviderSettings settings, string systemPromptBase, ILogger<OpenAIProvider> logger, string providerKey = "OpenAI")
    {
        ArgumentNullException.ThrowIfNull(settings);
        _providerName = providerKey;

        var endpoint = settings.Endpoint ?? throw new InvalidOperationException($"{providerKey}:Endpoint not configured");
        var apiKey = settings.ApiKey ?? throw new InvalidOperationException($"{providerKey}:ApiKey not configured");
        _modelName = settings.Model ?? "gpt-oss-120b";
        _systemPromptBase = systemPromptBase;

        var openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            Transport = new HttpClientPipelineTransport(new HttpClient(new LLMRequestLoggingHandler(logger)))
        });
        _chatClient = openAIClient.GetChatClient(_modelName);
    }

    public async Task<string> AskAsync(string systemPrompt, string userQuestion, string codeContext)
    {
        var messages = BuildMessages(systemPrompt, userQuestion, codeContext);
        var completion = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions { MaxOutputTokenCount = 8000, Temperature = 0.5f });
        return completion.Value.Content[0].Text;
    }

    public async Task<List<string>> ExtractKeywordsAsync(string question)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("Extract the most important search keywords from the user's question about code. Return a JSON array of 3-5 keywords/phrases. Return ONLY a JSON array."),
            new UserChatMessage(question)
        };

        var completion = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions { MaxOutputTokenCount = 300, Temperature = 0.5f });
        return ParseKeywords(completion.Value.Content[0].Text.Trim());
    }

    public async Task<string> ChatAsync(IList<ChatMessage> messages)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 8000,
            Temperature = 0.3f
        };

        var completion = await _chatClient.CompleteChatAsync(messages, options);
        var value = completion.Value;
        return value.Content.Count > 0 ? value.Content[0].Text ?? "" : "";
    }

    public async Task<LLMChatResponse> ChatWithToolsAsync(
        IList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 8000,
            Temperature = 0.3f
        };

        foreach (var tool in tools)
            options.Tools.Add(tool);

        var completion = await _chatClient.CompleteChatAsync(messages, options);
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
                AssistantMessage = new AssistantChatMessage(value.ToolCalls)
            };
        }

        var text = value.Content.Count > 0 ? value.Content[0].Text : "";
        return new LLMChatResponse
        {
            IsToolCall = false,
            TextContent = text ?? "",
            AssistantMessage = new AssistantChatMessage(text ?? "")
        };
    }

    private List<ChatMessage> BuildMessages(string systemPrompt, string userQuestion, string codeContext)
    {
        var fullSystemPrompt = string.IsNullOrEmpty(systemPrompt) ? _systemPromptBase : $"{_systemPromptBase}\n\n{systemPrompt}";
        var messages = new List<ChatMessage> { new SystemChatMessage(fullSystemPrompt) };

        var userContent = new System.Text.StringBuilder();
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

    private List<string> ParseKeywords(string response)
    {
        try
        {
            if (response.StartsWith("```"))
            {
                response = response.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")).Aggregate("", (a, b) => a + b);
            }
            return JsonSerializer.Deserialize<List<string>>(response) ?? new List<string>();
        }
        catch { return new List<string>(); }
    }
}
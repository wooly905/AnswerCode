using AnswerCode.Models;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using System.Text.Json;

namespace AnswerCode.Services.Providers;

public class AzureOpenAIProvider : ILLMProvider
{
    private readonly ChatClient _chatClient;
    private readonly string _systemPromptBase;

    public string Name => "AzureOpenAI";
    public bool SupportsToolCalling => true;

    public AzureOpenAIProvider(LLMProviderSettings settings, string systemPromptBase)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var endpoint = settings.Endpoint ?? throw new InvalidOperationException("AzureOpenAI:Endpoint not configured");
        var apiKey = settings.ApiKey ?? throw new InvalidOperationException("AzureOpenAI:ApiKey not configured");
        var deploymentName = settings.DeploymentName ?? throw new InvalidOperationException("AzureOpenAI:DeploymentName not configured");
        _systemPromptBase = systemPromptBase;

        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _chatClient = azureClient.GetChatClient(deploymentName);
    }

    public async Task<string> AskAsync(string systemPrompt, string userQuestion, string codeContext)
    {
        var fullSystemPrompt = string.IsNullOrEmpty(systemPrompt) ? _systemPromptBase : $"{_systemPromptBase}\n\n{systemPrompt}";
        var messages = new List<ChatMessage> { new SystemChatMessage(fullSystemPrompt) };

        var userContent = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(codeContext))
        {
            userContent.AppendLine("<code_context>\n" + codeContext + "\n</code_context>\n");
        }
        userContent.AppendLine("<question>\n" + userQuestion + "\n</question>");
        messages.Add(new UserChatMessage(userContent.ToString()));

        var completion = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions { MaxOutputTokenCount = 8000, Temperature = 0.5f });
        return completion.Value.Content[0].Text;
    }

    public async Task<List<string>> ExtractKeywordsAsync(string question)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("Extract keywords from the question and return a JSON array."),
            new UserChatMessage(question)
        };
        var completion = await _chatClient.CompleteChatAsync(messages);
        var response = completion.Value.Content[0].Text.Trim();
        try {
            if (response.StartsWith("```")) response = response.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")).Aggregate("", (a, b) => a + b);
            return JsonSerializer.Deserialize<List<string>>(response) ?? new List<string>();
        } catch { return new List<string>(); }
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
            Temperature = 0.5f
        };

        foreach (var tool in tools)
        {
            options.Tools.Add(tool);
        }

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
}
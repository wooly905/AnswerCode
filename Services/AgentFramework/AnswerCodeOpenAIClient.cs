using System.ClientModel;
using System.ClientModel.Primitives;
using AnswerCode.Models;
using AnswerCode.Services.Providers;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AnswerCode.Services.AgentFramework;

/// <summary>
/// Creates Microsoft.Extensions.AI chat clients for OpenAI-compatible and Azure OpenAI providers.
/// </summary>
public sealed class AnswerCodeOpenAIClient(ILogger<AnswerCodeOpenAIClient> logger)
{
    public IChatClient Create(string providerKey, LLMProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return ProviderKeys.Normalize(providerKey) == ProviderKeys.AzureOpenAI
            || !string.IsNullOrWhiteSpace(settings.DeploymentName)
            ? CreateAzureOpenAIClient(settings)
            : CreateOpenAICompatibleClient(providerKey, settings);
    }

    private IChatClient CreateOpenAICompatibleClient(string providerKey, LLMProviderSettings settings)
    {
        string endpoint = Require(settings.Endpoint, $"{providerKey}:Endpoint");
        string apiKey = Require(settings.ApiKey, $"{providerKey}:ApiKey");
        string model = Require(settings.Model, $"{providerKey}:Model");

        var chatClient = new OpenAI.Chat.ChatClient(
            credential: new ApiKeyCredential(apiKey),
            model: model,
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
                Transport = CreateTransport()
            });

        return chatClient.AsIChatClient();
    }

    private IChatClient CreateAzureOpenAIClient(LLMProviderSettings settings)
    {
        string endpoint = Require(settings.Endpoint, "AzureOpenAI:Endpoint");
        string apiKey = Require(settings.ApiKey, "AzureOpenAI:ApiKey");
        string deploymentName = Require(settings.DeploymentName, "AzureOpenAI:DeploymentName");

        var options = new AzureOpenAIClientOptions
        {
            Transport = CreateTransport()
        };

        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey), options);
        return azureClient.GetChatClient(deploymentName).AsIChatClient();
    }

    private HttpClientPipelineTransport CreateTransport() =>
        new(new HttpClient(new LLMRequestLoggingHandler(logger)));

    private static string Require(string? value, string settingName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{settingName} not configured");
}
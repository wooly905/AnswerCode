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

        var openAIClient = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
                Transport = CreateTransport()
            });

#pragma warning disable OPENAI001
        // store=false keeps history client-side, so each turn sends full input instead of previous_response_id.
        return openAIClient.GetResponsesClient().AsIChatClient(model)
            .AsBuilder()
            .ConfigureOptions(options => options.RawRepresentationFactory ??=
                _ => new OpenAI.Responses.CreateResponseOptions { StoredOutputEnabled = false })
            .Build();
#pragma warning restore OPENAI001
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
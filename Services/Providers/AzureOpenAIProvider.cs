using System.ClientModel.Primitives;
using AnswerCode.Models;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace AnswerCode.Services.Providers;

/// <summary>
/// Azure OpenAI provider. Uses the Azure SDK for authentication and deployment management.
/// All chat/tool-calling logic is inherited from <see cref="BaseLLMProvider"/>.
/// </summary>
public class AzureOpenAIProvider(LLMProviderSettings settings,
                                 string systemPromptBase,
                                 ILogger<AzureOpenAIProvider> logger) : BaseLLMProvider(CreateChatClient(settings, logger), systemPromptBase)
{
    public override string Name => ProviderKeys.AzureOpenAI;

    private static ChatClient CreateChatClient(LLMProviderSettings settings, ILogger<AzureOpenAIProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var endpoint = settings.Endpoint ?? throw new InvalidOperationException("AzureOpenAI:Endpoint not configured");
        var apiKey = settings.ApiKey ?? throw new InvalidOperationException("AzureOpenAI:ApiKey not configured");
        var deploymentName = settings.DeploymentName ?? throw new InvalidOperationException("AzureOpenAI:DeploymentName not configured");

        var loggingHandler = new LLMRequestLoggingHandler(logger);
        var options = new AzureOpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(loggingHandler))
        };

        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey), options);
        return azureClient.GetChatClient(deploymentName);
    }
}
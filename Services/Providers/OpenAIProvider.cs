using System.ClientModel;
using System.ClientModel.Primitives;
using AnswerCode.Models;
using OpenAI;
using OpenAI.Chat;

namespace AnswerCode.Services.Providers;

/// <summary>
/// OpenAI-compatible provider. Also used as the default for any non-Azure provider
/// (e.g., Ollama, local endpoints) since they expose OpenAI-compatible APIs.
/// </summary>
public class OpenAIProvider(LLMProviderSettings settings,
                            string systemPromptBase,
                            ILogger<OpenAIProvider> logger,
                            string providerKey = ProviderKeys.OpenAI) : BaseLLMProvider(CreateChatClient(settings, logger, providerKey), systemPromptBase)
{
    public override string Name => providerKey;

    private static ChatClient CreateChatClient(LLMProviderSettings settings, ILogger<OpenAIProvider> logger, string providerKey)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var endpoint = settings.Endpoint ?? throw new InvalidOperationException($"{providerKey}:Endpoint not configured");
        var apiKey = settings.ApiKey ?? throw new InvalidOperationException($"{providerKey}:ApiKey not configured");
        var modelName = settings.Model ?? "gpt-oss-120b";

        var openAIClient = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            Transport = new HttpClientPipelineTransport(new HttpClient(new LLMRequestLoggingHandler(logger)))
        });
        return openAIClient.GetChatClient(modelName);
    }
}
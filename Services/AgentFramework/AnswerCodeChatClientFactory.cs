using AnswerCode.Models;
using AnswerCode.Services.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace AnswerCode.Services.AgentFramework;

/// <summary>
/// Resolves configured providers to the corresponding AnswerCode IChatClient implementation.
/// </summary>
public sealed class AnswerCodeChatClientFactory(
    IOptions<LLMSettings> options,
    AnswerCodeOpenAIClient openAIClient,
    AnswerCodeFoundryClient foundryClient) : IAnswerCodeChatClientFactory
{
    private readonly LLMSettings _settings = options.Value;

    public IChatClient Create(string? providerName = null)
    {
        string providerKey = ResolveProviderKey(providerName);
        LLMProviderSettings providerSettings = _settings.Providers[providerKey];

        return ProviderKeys.Normalize(providerKey) == ProviderKeys.Foundry
            ? foundryClient.CreateChatClient(providerSettings)
            : openAIClient.Create(providerKey, providerSettings);
    }

    private string ResolveProviderKey(string? providerName)
    {
        string requested = string.IsNullOrWhiteSpace(providerName)
            ? _settings.DefaultProvider
            : providerName.Trim();
        string normalized = ProviderKeys.Normalize(requested);

        if (_settings.Providers.ContainsKey(normalized))
        {
            return normalized;
        }

        if (_settings.Providers.ContainsKey(requested))
        {
            return requested;
        }

        throw new InvalidOperationException(
            $"LLM provider '{requested}' is not configured. Available providers: {string.Join(", ", _settings.Providers.Keys)}");
    }
}
using AnswerCode.Models;

namespace AnswerCode.Services.Providers;

/// <summary>
/// Factory interface for creating LLM provider instances.
/// Implementations handle specific provider types (e.g., OpenAI, AzureOpenAI).
/// Adding a new provider requires only a new implementation + DI registration — no changes to LLMServiceFactory.
/// </summary>
public interface ILLMProviderCreator
{
    /// <summary>
    /// Whether this creator can handle the given provider key and settings.
    /// </summary>
    bool CanCreate(string providerKey, LLMProviderSettings settings);

    /// <summary>
    /// Create a provider instance for the given key and settings.
    /// </summary>
    ILLMProvider Create(string providerKey, LLMProviderSettings settings, string systemPromptBase);
}

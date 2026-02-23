using AnswerCode.Models;

namespace AnswerCode.Services.Providers;

/// <summary>
/// Creates <see cref="AzureOpenAIProvider"/> instances.
/// </summary>
public class AzureOpenAIProviderCreator(ILoggerFactory loggerFactory) : ILLMProviderCreator
{
    public bool CanCreate(string providerKey) => providerKey.Equals(ProviderKeys.AzureOpenAI, StringComparison.OrdinalIgnoreCase);

    public ILLMProvider Create(string providerKey, LLMProviderSettings settings, string systemPromptBase) => new AzureOpenAIProvider(settings, systemPromptBase, loggerFactory.CreateLogger<AzureOpenAIProvider>());
}

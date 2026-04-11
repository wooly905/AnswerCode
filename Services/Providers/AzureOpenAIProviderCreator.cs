using AnswerCode.Models;

namespace AnswerCode.Services.Providers;

/// <summary>
/// Creates <see cref="AzureOpenAIProvider"/> instances.
/// </summary>
public class AzureOpenAIProviderCreator(ILoggerFactory loggerFactory) : ILLMProviderCreator
{
    public bool CanCreate(string providerKey, LLMProviderSettings settings) => !string.IsNullOrEmpty(settings.DeploymentName);

    public ILLMProvider Create(string providerKey, LLMProviderSettings settings, string systemPromptBase) => new AzureOpenAIProvider(settings, systemPromptBase, loggerFactory.CreateLogger<AzureOpenAIProvider>(), providerKey);
}

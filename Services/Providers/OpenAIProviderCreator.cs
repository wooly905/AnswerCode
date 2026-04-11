using AnswerCode.Models;

namespace AnswerCode.Services.Providers;

/// <summary>
/// Creates <see cref="OpenAIProvider"/> instances.
/// Acts as the fallback for any provider key that is not specifically handled by another creator.
/// </summary>
public class OpenAIProviderCreator : ILLMProviderCreator
{
    private readonly ILoggerFactory _loggerFactory;

    public OpenAIProviderCreator(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Returns true for any provider key that is NOT AzureOpenAI (OpenAI-compatible fallback).
    /// </summary>
    public bool CanCreate(string providerKey, LLMProviderSettings settings) => string.IsNullOrEmpty(settings.DeploymentName);

    public ILLMProvider Create(string providerKey,
                               LLMProviderSettings settings,
                               string systemPromptBase) => new OpenAIProvider(settings,
                                                                              systemPromptBase,
                                                                              _loggerFactory.CreateLogger<OpenAIProvider>(),
                                                                              providerKey);
}

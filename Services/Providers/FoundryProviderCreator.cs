using AnswerCode.Models;
using AnswerCode.Services.AgentFramework;

namespace AnswerCode.Services.Providers;

public sealed class FoundryProviderCreator(AnswerCodeFoundryClient foundryClient) : ILLMProviderCreator
{
    public bool CanCreate(string providerKey, LLMProviderSettings settings) =>
        ProviderKeys.Normalize(providerKey) == ProviderKeys.Foundry;

    public ILLMProvider Create(
        string providerKey,
        LLMProviderSettings settings,
        string systemPromptBase) =>
        new FoundryProvider(
            providerKey,
            settings,
            systemPromptBase,
            foundryClient);
}
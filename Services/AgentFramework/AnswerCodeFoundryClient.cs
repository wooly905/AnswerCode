using AnswerCode.Models;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Extensions.AI;

namespace AnswerCode.Services.AgentFramework;

/// <summary>
/// Creates Microsoft Foundry project clients and Responses chat clients.
/// </summary>
public sealed class AnswerCodeFoundryClient(TokenCredential credential)
{
    public AIProjectClient CreateProjectClient(LLMProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string endpoint = Require(settings.Endpoint, "Foundry:Endpoint");
        return new AIProjectClient(new Uri(endpoint), credential);
    }

    public IChatClient CreateChatClient(LLMProviderSettings settings)
    {
        string model = Require(settings.Model ?? settings.DeploymentName, "Foundry:Model");
        AIProjectClient projectClient = CreateProjectClient(settings);

#pragma warning disable OPENAI001
        var responsesClient = projectClient.GetProjectOpenAIClient().GetResponsesClient();
        return responsesClient.AsIChatClient(model);
#pragma warning restore OPENAI001
    }

    private static string Require(string? value, string settingName) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{settingName} not configured");
}
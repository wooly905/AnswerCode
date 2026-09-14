using AnswerCode.Models;
using AnswerCode.Services.AgentFramework;
using AnswerCode.Services.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnswerCode.Tests.Services.AgentFramework;

public class AgentFrameworkConfigurationTests
{
    [Theory]
    [InlineData("Foundry")]
    [InlineData("foundry")]
    [InlineData("azure-foundry")]
    [InlineData("azurefoundry")]
    public void Normalize_FoundryAliases_ReturnsCanonicalKey(string providerName)
    {
        Assert.Equal(ProviderKeys.Foundry, ProviderKeys.Normalize(providerName));
    }

    [Fact]
    public void OpenAIFallback_DoesNotClaimFoundryProvider()
    {
        var creator = new OpenAIProviderCreator(NullLoggerFactory.Instance);

        bool canCreate = creator.CanCreate(
            ProviderKeys.Foundry,
            new LLMProviderSettings { Endpoint = "https://example.test", Model = "model" });

        Assert.False(canCreate);
    }

    [Fact]
    public void OpenAIClient_CustomKeyWithDeployment_UsesAzureSettings()
    {
        var client = new AnswerCodeOpenAIClient(NullLogger<AnswerCodeOpenAIClient>.Instance);
        var settings = new LLMProviderSettings
        {
            Endpoint = "https://example.test",
            DeploymentName = "gpt-5.4"
        };

        var error = Assert.Throws<InvalidOperationException>(() => client.Create("gpt-5.4", settings));

        Assert.Contains("AzureOpenAI:ApiKey", error.Message);
    }

    [Fact]
    public void OpenAIClient_CustomKeyWithoutDeployment_UsesCompatibleSettings()
    {
        var client = new AnswerCodeOpenAIClient(NullLogger<AnswerCodeOpenAIClient>.Instance);
        var settings = new LLMProviderSettings
        {
            Endpoint = "https://example.test",
            Model = "local-model"
        };

        var error = Assert.Throws<InvalidOperationException>(() => client.Create("custom", settings));

        Assert.Contains("custom:ApiKey", error.Message);
    }
}
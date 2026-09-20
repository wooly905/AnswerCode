using AnswerCode.Models;
using AnswerCode.Services.Agents;
using Xunit;

namespace AnswerCode.Tests.Services.Agents;

public class AgentPromptCatalogTests
{
    [Theory]
    [InlineData(AnswerRole.Developer, "expert code analyst")]
    [InlineData(AnswerRole.PM, "helping a Program Manager")]
    [InlineData(AnswerRole.CustomerService, "helping customer service representatives")]
    public void ForRole_ReturnsRoleSpecificPrompt(AnswerRole role, string expectedText)
    {
        Assert.Contains(expectedText, AgentPromptCatalog.ForRole(role));
    }

    [Fact]
    public void CustomerServicePrompt_DefinesSupportOutputAndSafetyBoundaries()
    {
        string prompt = AgentPromptCatalog.ForRole(AnswerRole.CustomerService);

        Assert.Contains("**Customer Response**", prompt);
        Assert.Contains("**Troubleshooting Steps**", prompt);
        Assert.Contains("**Information to Collect**", prompt);
        Assert.Contains("**Escalation Criteria**", prompt);
        Assert.Contains("Never invent product policy", prompt);
        Assert.Contains("Protect sensitive information", prompt);
        Assert.Contains("Do not include source code", prompt);
    }

    [Fact]
    public void CustomerServiceSynthesis_PreservesSupportContract()
    {
        string prompt = AgentPromptCatalog.SynthesisForRole(AnswerRole.CustomerService);

        Assert.Contains("**Customer Response**", prompt);
        Assert.Contains("**Escalation Criteria**", prompt);
        Assert.Contains("Do NOT invent refunds", prompt);
        Assert.Contains("Do NOT include source code", prompt);
    }

    [Fact]
    public void ForRole_WithUnsupportedValue_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentPromptCatalog.ForRole((AnswerRole)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentPromptCatalog.SynthesisForRole((AnswerRole)999));
    }
}
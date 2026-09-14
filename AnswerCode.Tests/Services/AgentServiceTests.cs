using AnswerCode.Models;
using AnswerCode.Services;
using AnswerCode.Services.Agents;
using AnswerCode.Services.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AnswerCode.Tests.Services;

public class AgentServiceTests : IDisposable
{
    private readonly string _rootPath = Directory.CreateTempSubdirectory("AnswerCodeAgent_").FullName;

    [Fact]
    public async Task RunAsync_WithoutHistory_DelegatesResearchWithSimpleBudget()
    {
        var provider = Mock.Of<ILLMProvider>(item => item.Name == "provider" && item.SupportsToolCalling);
        var factory = new Mock<ILLMServiceFactory>();
        factory.Setup(item => item.GetProvider("provider")).Returns(provider);
        var contextExpansion = new Mock<IContextExpansionService>();
        contextExpansion.Setup(item => item.BuildSymbolContextAsync(_rootPath, It.IsAny<string>(), default))
            .ReturnsAsync((string?)null);
        var conversationContext = new Mock<IConversationContextService>();
        var research = new Mock<IAgentResearchService>();
        research.Setup(item => item.RunAsync(
                It.IsAny<string>(),
                _rootPath,
                provider,
                It.IsAny<Func<AgentEvent, Task>>(),
                "Developer",
                It.IsAny<string>(),
                "session",
                8,
                null))
            .ReturnsAsync(new AgentResult { Answer = "answer" });
        var service = new AgentService(
            NullLogger<AgentService>.Instance,
            factory.Object,
            contextExpansion.Object,
            conversationContext.Object,
            research.Object,
            Options.Create(new AgentSettings()));
        var events = new List<AgentEvent>();

        AgentResult result = await service.RunAsync(
            "Where is Foo defined?",
            _rootPath,
            evt =>
            {
                events.Add(evt);
                return Task.CompletedTask;
            },
            "session",
            "provider",
            "Developer",
            []);

        Assert.Equal("answer", result.Answer);
        Assert.Equal(nameof(QuestionComplexity.Simple), result.ComplexityLabel);
        Assert.Equal(AgentEventType.Started, Assert.Single(events).Type);
        research.VerifyAll();
        conversationContext.VerifyNoOtherCalls();
    }

    public void Dispose() => Directory.Delete(_rootPath, recursive: true);
}
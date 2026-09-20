using AnswerCode.Models;
using AnswerCode.Services;
using AnswerCode.Services.Questions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AnswerCode.Tests.Services.Questions;

public class QuestionExecutionServiceTests
{
    [Theory]
    [InlineData(null, AnswerRole.Developer)]
    [InlineData(AnswerRole.PM, AnswerRole.PM)]
    [InlineData(AnswerRole.CustomerService, AnswerRole.CustomerService)]
    public async Task ExecuteAsync_MapsResultPersistsConversationAndResolvesRole(
        AnswerRole? requestedRole,
        AnswerRole expectedRole)
    {
        var agent = new Mock<IAgentService>();
        var history = new ConversationHistoryService();
        var request = new QuestionRequest
        {
            Question = "What is this project?",
            ProjectPath = "project",
            SessionId = "session-1",
            ModelProvider = "provider",
            UserRole = requestedRole
        };
        agent.Setup(service => service.RunAsync(
                request.Question,
                "resolved-project",
                request.SessionId,
                request.ModelProvider,
                expectedRole,
                It.IsAny<List<ConversationTurn>>()))
            .ReturnsAsync(new AgentResult
            {
                Answer = "An answer",
                TotalToolCalls = 2,
                IterationCount = 3,
                TotalInputTokens = 10,
                TotalOutputTokens = 4
            });
        var service = new QuestionExecutionService(
            agent.Object,
            history,
            NullLogger<QuestionExecutionService>.Instance);

        AnswerResponse response = await service.ExecuteAsync(request, "resolved-project");

        Assert.Equal("An answer", response.Answer);
        Assert.Equal(2, response.ToolCallCount);
        Assert.Equal(3, response.IterationCount);
        Assert.Equal("session-1", response.SessionId);
        Assert.Collection(
            history.GetHistory("session-1"),
            turn => Assert.Equal("user", turn.Role),
            turn => Assert.Equal("assistant", turn.Role));
    }
}
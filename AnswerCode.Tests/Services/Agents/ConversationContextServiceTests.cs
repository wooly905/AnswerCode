using AnswerCode.Models;
using AnswerCode.Services;
using AnswerCode.Services.Agents;
using AnswerCode.Services.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AnswerCode.Tests.Services.Agents;

public class ConversationContextServiceTests
{
    [Fact]
    public async Task PrepareHistoryAsync_CompressionFailureAboveHardCap_Throws()
    {
        var provider = new Mock<ILLMProvider>();
        provider.Setup(item => item.ChatAsync(It.IsAny<IList<OpenAI.Chat.ChatMessage>>()))
            .ThrowsAsync(new InvalidOperationException("compression failed"));
        var service = new ConversationContextService(
            new ConversationHistoryService(),
            NullLogger<ConversationContextService>.Instance);
        var history = new List<ConversationTurn>
        {
            new() { Role = "user", Content = new string('x', 610_000) },
            new() { Role = "assistant", Content = "answer" },
            new() { Role = "user", Content = "follow-up" },
            new() { Role = "assistant", Content = "answer" }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareHistoryAsync(provider.Object, history, "session"));

        Assert.Contains("200,000-token limit", exception.Message);
    }

    [Fact]
    public async Task PrepareHistoryAsync_BelowCompressionThreshold_ReturnsOriginalHistory()
    {
        var service = new ConversationContextService(
            new ConversationHistoryService(),
            NullLogger<ConversationContextService>.Instance);
        var history = new List<ConversationTurn>
        {
            new() { Role = "user", Content = "question" },
            new() { Role = "assistant", Content = "answer" }
        };

        List<ConversationTurn> result = await service.PrepareHistoryAsync(
            Mock.Of<ILLMProvider>(),
            history,
            "session");

        Assert.Same(history, result);
    }
}
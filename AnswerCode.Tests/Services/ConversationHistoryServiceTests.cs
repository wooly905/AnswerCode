using AnswerCode.Services;

namespace AnswerCode.Tests.Services;

public class ConversationHistoryServiceTests
{
    [Fact]
    public void GetHistory_UnknownSession_ReturnsEmptyList()
    {
        var service = new ConversationHistoryService();

        var history = service.GetHistory("unknown-session");

        Assert.Empty(history);
    }

    [Fact]
    public void AddTurn_ThenGetHistory_ReturnsTurnsInOrder()
    {
        var service = new ConversationHistoryService();
        var sessionId = "session-1";

        service.AddTurn(sessionId, new ConversationTurn { Role = "user", Content = "Question 1" });
        service.AddTurn(sessionId, new ConversationTurn { Role = "assistant", Content = "Answer 1" });

        var history = service.GetHistory(sessionId);

        Assert.Equal(2, history.Count);
        Assert.Equal("user", history[0].Role);
        Assert.Equal("Question 1", history[0].Content);
        Assert.Equal("assistant", history[1].Role);
        Assert.Equal("Answer 1", history[1].Content);
    }

    [Fact]
    public void GetHistory_ReturnsCopy_NotLiveReference()
    {
        var service = new ConversationHistoryService();
        var sessionId = "session-1";
        service.AddTurn(sessionId, new ConversationTurn { Role = "user", Content = "Q1" });

        var history = service.GetHistory(sessionId);
        history.Add(new ConversationTurn { Role = "user", Content = "Should not persist" });

        var historyAgain = service.GetHistory(sessionId);
        Assert.Single(historyAgain);
    }

    [Fact]
    public void Sessions_AreIsolatedFromEachOther()
    {
        var service = new ConversationHistoryService();
        service.AddTurn("session-a", new ConversationTurn { Role = "user", Content = "A" });
        service.AddTurn("session-b", new ConversationTurn { Role = "user", Content = "B" });

        Assert.Single(service.GetHistory("session-a"));
        Assert.Single(service.GetHistory("session-b"));
        Assert.Equal("A", service.GetHistory("session-a")[0].Content);
        Assert.Equal("B", service.GetHistory("session-b")[0].Content);
    }

    [Fact]
    public void ReplaceTurns_OverwritesExistingHistory()
    {
        var service = new ConversationHistoryService();
        var sessionId = "session-1";
        service.AddTurn(sessionId, new ConversationTurn { Role = "user", Content = "Q1" });
        service.AddTurn(sessionId, new ConversationTurn { Role = "assistant", Content = "A1" });

        service.ReplaceTurns(sessionId, [new ConversationTurn { Role = "assistant", Content = "Summary", IsSummary = true }]);

        var history = service.GetHistory(sessionId);
        var turn = Assert.Single(history);
        Assert.True(turn.IsSummary);
        Assert.Equal("Summary", turn.Content);
    }

    [Fact]
    public void ReplaceTurns_UnknownSession_CreatesSessionWithNewTurns()
    {
        var service = new ConversationHistoryService();

        service.ReplaceTurns("new-session", [new ConversationTurn { Role = "user", Content = "Hi" }]);

        Assert.Single(service.GetHistory("new-session"));
    }

    [Fact]
    public void Clear_RemovesAllHistoryForSession()
    {
        var service = new ConversationHistoryService();
        var sessionId = "session-1";
        service.AddTurn(sessionId, new ConversationTurn { Role = "user", Content = "Q1" });

        service.Clear(sessionId);

        Assert.Empty(service.GetHistory(sessionId));
    }

    [Fact]
    public void Clear_UnknownSession_DoesNotThrow()
    {
        var service = new ConversationHistoryService();

        var exception = Record.Exception(() => service.Clear("never-existed"));

        Assert.Null(exception);
    }
}

using System.Collections.Concurrent;

namespace AnswerCode.Services;

/// <summary>
/// Stores conversation history per session so users can ask follow-up questions.
/// In-memory implementation — history is lost on app restart.
/// History length is controlled by token budget (not fixed turn count).
/// </summary>
public interface IConversationHistoryService
{
    /// <summary>Get all turns for a session (empty list if none).</summary>
    List<ConversationTurn> GetHistory(string sessionId);

    /// <summary>Append a turn (user question or assistant answer) to the session.</summary>
    void AddTurn(string sessionId, ConversationTurn turn);

    /// <summary>Replace all turns for a session (used after history compression).</summary>
    void ReplaceTurns(string sessionId, List<ConversationTurn> newTurns);

    /// <summary>Clear all history for a session.</summary>
    void Clear(string sessionId);
}

public class ConversationHistoryService : IConversationHistoryService
{
    private readonly ConcurrentDictionary<string, List<ConversationTurn>> _store = new();

    public List<ConversationTurn> GetHistory(string sessionId)
    {
        if (_store.TryGetValue(sessionId, out var turns))
        {
            lock (turns)
            {
                return [.. turns];
            }
        }
        return [];
    }

    public void AddTurn(string sessionId, ConversationTurn turn)
    {
        var turns = _store.GetOrAdd(sessionId, _ => []);
        lock (turns)
        {
            turns.Add(turn);
        }
    }

    public void ReplaceTurns(string sessionId, List<ConversationTurn> newTurns)
    {
        var turns = _store.GetOrAdd(sessionId, _ => []);
        lock (turns)
        {
            turns.Clear();
            turns.AddRange(newTurns);
        }
    }

    public void Clear(string sessionId)
    {
        _store.TryRemove(sessionId, out _);
    }
}

/// <summary>
/// A single turn in the conversation (either a user question or an assistant answer).
/// </summary>
public class ConversationTurn
{
    public required string Role { get; init; } // "user" or "assistant"
    public required string Content { get; init; }

    /// <summary>
    /// True if this turn is a compressed summary of older conversation turns.
    /// </summary>
    public bool IsSummary { get; init; }
}

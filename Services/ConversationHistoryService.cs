using System.Collections.Concurrent;

namespace AnswerCode.Services;

/// <summary>
/// Stores conversation history per session so users can ask follow-up questions.
/// In-memory implementation — history is lost on app restart.
/// </summary>
public interface IConversationHistoryService
{
    /// <summary>Get all turns for a session (empty list if none).</summary>
    List<ConversationTurn> GetHistory(string sessionId);

    /// <summary>Append a turn (user question or assistant answer) to the session.</summary>
    void AddTurn(string sessionId, ConversationTurn turn);

    /// <summary>Clear all history for a session.</summary>
    void Clear(string sessionId);
}

public class ConversationHistoryService : IConversationHistoryService
{
    /// <summary>Max Q&A pairs to keep per session (oldest are trimmed).</summary>
    private const int MaxTurnsPerSession = 20; // 10 Q&A rounds

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

            // Trim oldest turns if over limit, keeping pairs intact
            while (turns.Count > MaxTurnsPerSession)
            {
                turns.RemoveAt(0);
            }
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
}

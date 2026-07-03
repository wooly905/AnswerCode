namespace AnswerCode.Services;

/// <summary>
/// Coordinates "ask the user a clarifying question" requests from the agent tool loop
/// with the answer submitted later by the client through a separate HTTP call.
/// </summary>
public interface IUserInputService
{
    /// <summary>
    /// Register a pending question and wait for the answer (or timeout).
    /// </summary>
    /// <param name="questionId">Unique id correlating this question with its future answer.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <returns>The user's answer, or null if the question timed out or was cancelled.</returns>
    Task<string?> WaitForAnswerAsync(string questionId, TimeSpan timeout);

    /// <summary>
    /// Submit an answer for a pending question. Returns false if there is no such pending question
    /// (e.g. it already timed out or was already answered).
    /// </summary>
    bool SubmitAnswer(string questionId, string answer);
}

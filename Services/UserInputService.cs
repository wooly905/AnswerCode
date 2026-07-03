using System.Collections.Concurrent;

namespace AnswerCode.Services;

/// <summary>
/// In-memory implementation of <see cref="IUserInputService"/>. Pending questions are kept
/// in a process-local dictionary keyed by question id; suitable for a single-instance deployment.
/// </summary>
public class UserInputService(ILogger<UserInputService> logger) : IUserInputService
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pending = new();

    public async Task<string?> WaitForAnswerAsync(string questionId, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(questionId, tcs))
        {
            logger.LogWarning("Duplicate question id {QuestionId}", questionId);
            return null;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await using var registration = cts.Token.Register(() => tcs.TrySetResult(null));
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(questionId, out _);
        }
    }

    public bool SubmitAnswer(string questionId, string answer)
    {
        if (_pending.TryGetValue(questionId, out var tcs))
        {
            return tcs.TrySetResult(answer);
        }

        logger.LogWarning("No pending question found for id {QuestionId} (may have expired)", questionId);
        return false;
    }
}

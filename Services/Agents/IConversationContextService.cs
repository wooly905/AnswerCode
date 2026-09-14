using AnswerCode.Services.Providers;

namespace AnswerCode.Services.Agents;

public interface IConversationContextService
{
    Task<List<ConversationTurn>> PrepareHistoryAsync(
        ILLMProvider provider,
        List<ConversationTurn> history,
        string? sessionId);

    Task<(string Question, int InputTokens, int OutputTokens)> ResolveQuestionAsync(
        ILLMProvider provider,
        string question,
        List<ConversationTurn> history);

    Task<(string Answer, int InputTokens, int OutputTokens)> SynthesizeAnswerAsync(
        ILLMProvider provider,
        string question,
        List<ConversationTurn> history,
        string findings,
        string? userRole);
}
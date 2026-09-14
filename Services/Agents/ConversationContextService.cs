using AnswerCode.Services.Providers;
using AnswerCode.Services.Tools;
using OpenAI.Chat;

namespace AnswerCode.Services.Agents;

public sealed class ConversationContextService(
    IConversationHistoryService historyService,
    ILogger<ConversationContextService> logger) : IConversationContextService
{
    internal const int MaxHistoryTokens = 200_000;
    internal const int CompressAtTokens = 180_000;
    private const double KeepRecentFraction = 0.2;

    public async Task<List<ConversationTurn>> PrepareHistoryAsync(
        ILLMProvider provider,
        List<ConversationTurn> history,
        string? sessionId)
    {
        int estimatedTokens = EstimateTokens(history);
        logger.LogInformation("Estimated history tokens: {Tokens} ({Turns} turns)", estimatedTokens, history.Count);
        if (estimatedTokens < CompressAtTokens)
        {
            return history;
        }

        try
        {
            List<ConversationTurn> compressed = await CompressAsync(provider, history).ConfigureAwait(false);
            int compressedTokens = EstimateTokens(compressed);
            if (compressedTokens >= MaxHistoryTokens)
            {
                throw new InvalidOperationException(
                    $"Conversation history remains above the {MaxHistoryTokens:N0}-token limit after compression.");
            }

            if (sessionId is not null)
            {
                historyService.ReplaceTurns(sessionId, compressed);
            }

            logger.LogInformation(
                "History compressed: {OldTokens} -> {NewTokens} tokens, {Turns} turns",
                estimatedTokens,
                compressedTokens,
                compressed.Count);
            return compressed;
        }
        catch (Exception ex) when (estimatedTokens < MaxHistoryTokens)
        {
            logger.LogWarning(ex, "History compression failed, proceeding with uncompressed history");
            return history;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Conversation history exceeds the {MaxHistoryTokens:N0}-token limit and could not be compressed.",
                ex);
        }
    }

    public async Task<(string Question, int InputTokens, int OutputTokens)> ResolveQuestionAsync(
        ILLMProvider provider,
        string question,
        List<ConversationTurn> history)
    {
        var messages = new List<ChatMessage> { new SystemChatMessage(AgentPromptCatalog.ContextResolution) };
        InjectHistory(messages, history);
        messages.Add(new UserChatMessage($"## Current Question (rewrite this as self-contained)\n{question}"));

        var response = await provider.ChatAsync(messages).ConfigureAwait(false);
        string resolved = ReActParser.CleanNativeTokens(response.TextContent?.Trim() ?? question);
        return (string.IsNullOrWhiteSpace(resolved) ? question : resolved, response.InputTokens, response.OutputTokens);
    }

    public async Task<(string Answer, int InputTokens, int OutputTokens)> SynthesizeAnswerAsync(
        ILLMProvider provider,
        string question,
        List<ConversationTurn> history,
        string findings,
        string? userRole)
    {
        var messages = new List<ChatMessage> { new SystemChatMessage(AgentPromptCatalog.SynthesisForRole(userRole)) };
        InjectHistory(messages, history);
        messages.Add(new UserChatMessage($"## Current Question\n{question}\n\n## Research Findings\n{findings}"));

        var response = await provider.ChatAsync(messages).ConfigureAwait(false);
        return (response.TextContent ?? findings, response.InputTokens, response.OutputTokens);
    }

    internal static int EstimateTokens(List<ConversationTurn> history) =>
        (int)(history.Sum(turn => (long)turn.Content.Length) / 3);

    private async Task<List<ConversationTurn>> CompressAsync(
        ILLMProvider provider,
        List<ConversationTurn> history)
    {
        int keepCount = Math.Max(2, (int)(history.Count * KeepRecentFraction));
        if (keepCount % 2 != 0)
        {
            keepCount++;
        }

        keepCount = Math.Min(keepCount, history.Count);
        int compressCount = history.Count - keepCount;
        if (compressCount <= 0)
        {
            return history;
        }

        var conversation = new System.Text.StringBuilder();
        foreach (ConversationTurn turn in history.GetRange(0, compressCount))
        {
            string label = turn.IsSummary ? "Summary" : turn.Role == "user" ? "User" : "Assistant";
            conversation.AppendLine($"**{label}:** {turn.Content}");
            conversation.AppendLine();
        }

        var response = await provider.ChatAsync(
        [
            new SystemChatMessage(AgentPromptCatalog.Compression),
            new UserChatMessage(conversation.ToString())
        ]).ConfigureAwait(false);
        string? summary = response.TextContent?.Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException("History compression returned an empty summary.");
        }

        var compressed = new List<ConversationTurn>
        {
            new() { Role = "assistant", Content = summary, IsSummary = true }
        };
        compressed.AddRange(history.GetRange(compressCount, keepCount));
        return compressed;
    }

    private static void InjectHistory(List<ChatMessage> messages, List<ConversationTurn> history)
    {
        foreach (ConversationTurn turn in history)
        {
            messages.Add(turn.IsSummary
                ? new UserChatMessage($"[Summary of earlier conversation]\n{turn.Content}")
                : turn.Role == "user"
                    ? new UserChatMessage(turn.Content)
                    : new AssistantChatMessage(turn.Content));
        }
    }
}
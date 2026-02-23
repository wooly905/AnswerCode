using System.Collections.Concurrent;
using OpenAI.Chat;

namespace AnswerCode.Services;

/// <summary>
/// Main LLM Service that uses Factory Pattern to get the appropriate provider.
/// Inherits shared utilities (TruncateContext, ExtractKeywordsFallback) from BaseLLMService.
/// </summary>
public class LLMService : BaseLLMService, ILLMService
{
    private readonly ILogger<LLMService> _logger;
    private readonly ILLMServiceFactory _factory;
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _sessionHistory = new();

    public LLMService(ILogger<LLMService> logger, ILLMServiceFactory factory)
    {
        _logger = logger;
        _factory = factory;
    }

    public async Task<string> AskAsync(string systemPrompt, string userQuestion, string codeContext, string? sessionId = null, string? modelProvider = null)
    {
        var provider = _factory.GetProvider(modelProvider);
        _logger.LogInformation("Using provider: {Provider} for AskAsync", provider.Name);

        // Truncate context to avoid token limits
        var truncatedContext = TruncateContext(codeContext, 100000);

        // Call the provider
        var response = await provider.AskAsync(systemPrompt, userQuestion, truncatedContext);

        // Manage session history (Simple version: only for tracking)
        if (!string.IsNullOrEmpty(sessionId))
        {
            var history = _sessionHistory.GetOrAdd(sessionId, _ => new List<ChatMessage>());
            history.Add(new UserChatMessage(userQuestion));
            history.Add(new AssistantChatMessage(response));
            if (history.Count > 20)
            {
                history.RemoveRange(0, history.Count - 10);
            }
        }

        return response;
    }

    public async Task<List<string>> ExtractKeywordsAsync(string question, string? modelProvider = null)
    {
        var provider = _factory.GetProvider(modelProvider);
        _logger.LogInformation("Using provider: {Provider} for ExtractKeywordsAsync", provider.Name);

        try
        {
            var keywords = await provider.ExtractKeywordsAsync(question);
            return keywords.Count > 0 ? keywords : ExtractKeywordsFallback(question);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Keyword extraction failed, using fallback");
            return ExtractKeywordsFallback(question);
        }
    }
}
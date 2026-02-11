using System.Collections.Concurrent;
using OpenAI.Chat;

namespace AnswerCode.Services;

/// <summary>
/// Main LLM Service that uses Factory Pattern to get the appropriate provider
/// </summary>
public class LLMService : ILLMService
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

    private List<string> ExtractKeywordsFallback(string question)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "could", "should",
            "may", "might", "must", "shall", "can", "need", "dare", "ought", "used",
            "what", "which", "who", "whom", "whose", "where", "when", "why", "how",
            "this", "that", "these", "those", "here", "there",
            "and", "or", "but", "if", "then", "else", "for", "from", "to", "of", "in", "on", "at",
            "with", "about", "into", "through", "during", "before", "after",
            "please", "help", "want", "need", "like", "know", "tell", "show", "find", "look",
            "問題", "請問", "如何", "怎麼", "什麼", "哪裡", "為什麼", "可以", "能夠", "關於"
        };

        var words = question.Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '?', '!', ':', ';', '(', ')', '[', ']', '{', '}', '"', '\'' },
            StringSplitOptions.RemoveEmptyEntries);

        return words
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static string TruncateContext(string context, int maxLength)
    {
        if (context.Length <= maxLength)
        {
            return context;
        }

        string truncated = context.Substring(0, maxLength);
        int lastNewLine = truncated.LastIndexOf('\n');

        if (lastNewLine > maxLength * 0.8)
        {
            truncated = truncated[..lastNewLine];
        }

        return truncated + "\n\n[... context truncated for length ...]";
    }
}
namespace AnswerCode.Services;

/// <summary>
/// Base class for LLM services with shared utilities
/// </summary>
public abstract class BaseLLMService
{
    protected const string CodeAnalysisSystemPrompt = @"
You are an expert code analyst. Your task is to analyze code and answer questions about it.

Guidelines:
1. Be precise and cite specific file paths and line numbers when relevant
2. Focus on the code context provided - don't make assumptions about code you haven't seen
3. Use markdown formatting for code snippets and file references
4. If you're unsure about something, say so rather than guessing
5. Provide actionable insights when possible
6. Respond in the same language as the user's question (e.g., if the user asks in Taiwan Chinese, respond in Taiwan Chinese)

When analyzing code:
- Look for patterns, dependencies, and relationships between components
- Identify key classes, functions, and their purposes
- Note any potential issues or areas for improvement
- Explain the code flow when relevant
";

    protected static string TruncateContext(string context, int maxLength)
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

    protected List<string> ExtractKeywordsFallback(string question)
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

        var words = question.Split(
            [' ', '\t', '\n', '\r', ',', '.', '?', '!', ':', ';', '(', ')', '[', ']', '{', '}', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries);

        return words.Where(w => w.Length > 2 && !stopWords.Contains(w))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();
    }
}
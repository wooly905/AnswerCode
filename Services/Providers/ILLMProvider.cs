using AnswerCode.Models;
using OpenAI.Chat;

namespace AnswerCode.Services.Providers;

/// <summary>
/// Interface for specific LLM providers (OpenAI, Azure, etc.)
/// </summary>
public interface ILLMProvider
{
    /// <summary>
    /// Provider name (e.g., OpenAI, AzureOpenAI)
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether this provider supports tool calling (function calling)
    /// </summary>
    bool SupportsToolCalling { get; }

    /// <summary>
    /// Ask a question to the specific provider
    /// </summary>
    Task<string> AskAsync(string systemPrompt, string userQuestion, string codeContext);

    /// <summary>
    /// Extract keywords using the specific provider
    /// </summary>
    Task<List<string>> ExtractKeywordsAsync(string question);

    /// <summary>
    /// Multi-turn chat without tool definitions. Used by ReAct agent loop.
    /// Returns text content with token usage populated.
    /// </summary>
    Task<LLMChatResponse> ChatAsync(IList<ChatMessage> messages);
}
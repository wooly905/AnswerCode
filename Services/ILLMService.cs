namespace AnswerCode.Services;

/// <summary>
/// LLM Service Interface
/// </summary>
public interface ILLMService
{
    /// <summary>
    /// Ask a question to the LLM
    /// </summary>
    /// <param name="systemPrompt">System prompt</param>
    /// <param name="userQuestion">User question</param>
    /// <param name="codeContext">Code context</param>
    /// <param name="sessionId">Session ID (for maintaining conversation history)</param>
    /// <param name="modelProvider">Model provider name (OpenAI, AzureOpenAI)</param>
    /// <returns>LLM response</returns>
    public Task<string> AskAsync(string systemPrompt, string userQuestion, string codeContext, string? sessionId = null, string? modelProvider = null);

    /// <summary>
    /// Extract keywords from a question for searching
    /// </summary>
    /// <param name="question">User question</param>
    /// <param name="modelProvider">Model provider name (OpenAI, AzureOpenAI)</param>
    /// <returns>List of keywords</returns>
    public Task<List<string>> ExtractKeywordsAsync(string question, string? modelProvider = null);
}
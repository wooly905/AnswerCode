namespace AnswerCode.Services;

/// <summary>
/// Factory interface for creating LLM service providers
/// </summary>
public interface ILLMServiceFactory
{
    /// <summary>
    /// Get an LLM provider by name
    /// </summary>
    /// <param name="providerName">Provider name (OpenAI, AzureOpenAI)</param>
    /// <returns>LLM provider instance</returns>
    Providers.ILLMProvider GetProvider(string? providerName);

    /// <summary>
    /// Get all available provider names
    /// </summary>
    /// <returns>List of available provider names</returns>
    IEnumerable<string> GetAvailableProviders();

    /// <summary>
    /// Get provider display information (name and display name from config)
    /// </summary>
    /// <returns>Dictionary mapping provider key to display name</returns>
    Dictionary<string, string> GetProviderDisplayNames();
}
namespace AnswerCode.Services.Providers;

/// <summary>
/// Canonical provider key constants and normalization logic.
/// Use these instead of magic strings throughout the codebase.
/// </summary>
public static class ProviderKeys
{
    public const string OpenAI = "OpenAI";
    public const string AzureOpenAI = "AzureOpenAI";
    public const string Ollama = "Ollama";

    /// <summary>
    /// Normalize various user inputs (e.g., "azure", "open-ai") to canonical provider key names.
    /// </summary>
    public static string Normalize(string providerName)
    {
        return providerName.Trim().ToLowerInvariant() switch
        {
            "openai" or "open-ai" => OpenAI,
            "azure" or "azureopenai" or "azure-openai" => AzureOpenAI,
            "ollama" => Ollama,
            _ => providerName.Trim()
        };
    }
}

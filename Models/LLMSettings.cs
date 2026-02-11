namespace AnswerCode.Models;

/// <summary>
/// Root configuration for LLM providers.
/// Binds to the "LLM" section in appsettings.json.
/// </summary>
public class LLMSettings
{
    public const string SectionName = "LLM";

    /// <summary>
    /// Default provider to use when none is specified.
    /// </summary>
    public string DefaultProvider { get; set; } = "OpenAI";

    /// <summary>
    /// Provider-specific settings keyed by provider name (e.g., OpenAI, AzureOpenAI).
    /// </summary>
    public Dictionary<string, LLMProviderSettings> Providers { get; set; } = new();
}

/// <summary>
/// Settings for a single LLM provider.
/// Supports both Azure OpenAI (DeploymentName) and OpenAI-compatible (Model) endpoints.
/// </summary>
public class LLMProviderSettings
{
    /// <summary>
    /// API endpoint URL (e.g., Azure Cognitive Services URL or OpenAI API base URL).
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// API key for authentication. Consider using appsettings.Local.json for local overrides.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Azure OpenAI deployment name. Required for AzureOpenAI provider.
    /// </summary>
    public string? DeploymentName { get; set; }

    /// <summary>
    /// Model name for OpenAI-compatible endpoints (e.g., gpt-oss-120b).
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Optional display name for UI purposes.
    /// </summary>
    public string? DisplayName { get; set; }
}

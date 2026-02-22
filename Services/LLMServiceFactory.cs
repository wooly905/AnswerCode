using AnswerCode.Models;
using AnswerCode.Services.Providers;
using Microsoft.Extensions.Options;

namespace AnswerCode.Services;

/// <summary>
/// Factory implementation for creating LLM service providers
/// </summary>
public class LLMServiceFactory : ILLMServiceFactory
{
    private readonly LLMSettings _settings;
    private readonly ILogger<LLMServiceFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, ILLMProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    private const string CodeAnalysisSystemPrompt = @"
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

    public LLMServiceFactory(IOptions<LLMSettings> options, ILogger<LLMServiceFactory> logger, ILoggerFactory loggerFactory)
    {
        _settings = options.Value;
        _logger = logger;
        _loggerFactory = loggerFactory;

        if (_settings.Providers == null || _settings.Providers.Count == 0)
        {
            _logger.LogWarning("No LLM providers configured in LLM:Providers section");
            return;
        }

        foreach (var kvp in _settings.Providers)
        {
            var providerKey = kvp.Key;
            var providerSettings = kvp.Value;

            if (providerSettings == null || string.IsNullOrEmpty(providerSettings.Endpoint))
            {
                _logger.LogWarning("Skipping provider '{Key}': missing Endpoint", providerKey);
                continue;
            }

            try
            {
                if (providerKey.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    _providers[providerKey] = new AzureOpenAIProvider(providerSettings, CodeAnalysisSystemPrompt, _loggerFactory.CreateLogger<AzureOpenAIProvider>());
                }
                else
                {
                    _providers[providerKey] = new OpenAIProvider(providerSettings, CodeAnalysisSystemPrompt, _loggerFactory.CreateLogger<OpenAIProvider>(), providerKey);
                }
                _logger.LogInformation("✓ Initialized {Provider} provider", providerKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "✗ Failed to initialize {Provider} provider: {Message}", providerKey, ex.Message);
            }
        }

        if (_providers.Count == 0)
        {
            _logger.LogError("No LLM providers were successfully initialized!");
        }
        else
        {
            _logger.LogInformation("Successfully initialized {Count} provider(s): {Providers}",
                _providers.Count,
                string.Join(", ", _providers.Keys));
        }
    }

    public ILLMProvider GetProvider(string? providerName)
    {
        var normalizedName = NormalizeProviderName(providerName);

        if (!_providers.TryGetValue(normalizedName, out var provider))
        {
            var availableProviders = string.Join(", ", _providers.Keys);
            _logger.LogWarning("Provider '{RequestedProvider}' not found. Available providers: {AvailableProviders}. Falling back to first available provider.",
                               normalizedName,
                               availableProviders);

            // Fallback to first available provider
            provider = _providers.Values.FirstOrDefault();
            if (provider == null)
            {
                throw new InvalidOperationException($"No LLM providers are available. Please check your configuration in appsettings.json. Requested provider: '{normalizedName}'");
            }

            _logger.LogInformation("Using fallback provider: {FallbackProvider}", provider.Name);
        }
        else
        {
            _logger.LogDebug("Using provider: {Provider}", provider.Name);
        }

        return provider;
    }

    public IEnumerable<string> GetAvailableProviders()
    {
        return _providers.Keys;
    }

    public Dictionary<string, string> GetProviderDisplayNames()
    {
        var result = new Dictionary<string, string>();
        foreach (var kvp in _providers)
        {
            var key = kvp.Key;
            if (_settings.Providers?.TryGetValue(key, out var settings) == true && !string.IsNullOrEmpty(settings!.DisplayName))
            {
                result[key] = settings.DisplayName;
            }
            else
            {
                result[key] = key;
            }
        }
        return result;
    }

    private string NormalizeProviderName(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return _providers.ContainsKey(_settings.DefaultProvider)
                ? _settings.DefaultProvider
                : _providers.Keys.FirstOrDefault() ?? "OpenAI";
        }

        var normalized = providerName.Trim().ToLowerInvariant() switch
        {
            "openai" or "open-ai" => "OpenAI",
            "azure" or "azureopenai" or "azure-openai" => "AzureOpenAI",
            "ollama" => "Ollama",
            _ => providerName.Trim()
        };

        return _providers.ContainsKey(normalized) ? normalized : providerName.Trim();
    }
}
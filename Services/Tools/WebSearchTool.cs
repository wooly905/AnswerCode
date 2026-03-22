using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Web search tool — allows the LLM to search the internet via Tavily Search API
/// to retrieve external information (documentation, API references, latest updates, etc.)
/// </summary>
public class WebSearchTool : ITool
{
    public const string ToolName = "web_search";

    private readonly string? _apiKey;
    private readonly HttpClient _httpClient;

    public WebSearchTool(IConfiguration configuration)
    {
        _apiKey = configuration["Tavily:ApiKey"];
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public string Name => ToolName;

    public string Description =>
"""
Search the web for external information using a search engine.
Use this when the user's question requires knowledge beyond the codebase — for example:
- Documentation for a library, framework, or API used in the project
- Latest best practices, updates, or release notes
- General programming concepts or error messages
- Comparing the codebase's approach with industry standards
Do NOT use this tool for questions that can be answered by reading the codebase itself.
""";

    public ChatTool GetChatToolDefinition()
    {
        return ChatTool.CreateFunctionTool(
            functionName: Name,
            functionDescription: Description,
            functionParameters: BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    query = new
                    {
                        type = "string",
                        description = "The search query. Be specific and concise for best results."
                    },
                    search_depth = new
                    {
                        type = "string",
                        description = "Search depth: 'basic' for quick results (default), 'advanced' for more thorough search.",
                        @enum = new[] { "basic", "advanced" }
                    }
                },
                required = new[] { "query" }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return "Error: Web search is not configured. Tavily API key is missing from appsettings (Tavily:ApiKey).";
        }

        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
        var searchDepth = args.TryGetProperty("search_depth", out var sd) ? sd.GetString() : "basic";

        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: 'query' parameter is required.";
        }

        try
        {
            var payload = new
            {
                api_key = _apiKey,
                query,
                search_depth = searchDepth ?? "basic",
                max_results = 5
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("https://api.tavily.com/search", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return $"Error: Tavily API returned HTTP {(int)response.StatusCode}. {errorBody}";
            }

            var resultJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JsonElement>(resultJson);

            return FormatResults(query, result);
        }
        catch (TaskCanceledException)
        {
            return "Error: Web search timed out after 30 seconds.";
        }
        catch (Exception ex)
        {
            return $"Error: Web search failed — {ex.Message}";
        }
    }

    private static string FormatResults(string query, JsonElement result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Web search results for: \"{query}\"");
        sb.AppendLine();

        // Include the answer if Tavily provides one
        if (result.TryGetProperty("answer", out var answer))
        {
            var answerText = answer.GetString();
            if (!string.IsNullOrWhiteSpace(answerText))
            {
                sb.AppendLine("## Summary");
                sb.AppendLine(answerText);
                sb.AppendLine();
            }
        }

        // Include individual search results
        if (result.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var item in results.EnumerateArray())
            {
                if (i >= 5) break;
                i++;

                var title = item.TryGetProperty("title", out var t) ? t.GetString() : "(no title)";
                var url = item.TryGetProperty("url", out var u) ? u.GetString() : "";
                var snippet = item.TryGetProperty("content", out var c) ? c.GetString() : "";

                sb.AppendLine($"### [{i}] {title}");
                if (!string.IsNullOrEmpty(url)) sb.AppendLine($"URL: {url}");
                if (!string.IsNullOrEmpty(snippet))
                {
                    // Truncate very long snippets
                    var text = snippet.Length > 800 ? snippet[..800] + "..." : snippet;
                    sb.AppendLine(text);
                }
                sb.AppendLine();
            }

            if (i == 0)
            {
                sb.AppendLine("No results found.");
            }
        }
        else
        {
            sb.AppendLine("No results found.");
        }

        return sb.ToString();
    }
}

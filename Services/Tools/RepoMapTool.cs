using System.Text.Json;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Repository map tool — generates an architectural overview showing module boundaries,
/// roles, cross-module dependencies, and entry points. Helps the agent quickly understand
/// how a codebase is organized without multiple exploration iterations.
/// </summary>
public class RepoMapTool : ITool
{
    public const string ToolName = "repo_map";

    private readonly IRepoMapService _repoMapService;

    public RepoMapTool(IRepoMapService repoMapService)
    {
        _repoMapService = repoMapService;
    }

    public string Name => ToolName;

    public string Description =>
"""
Generate a repository map showing module boundaries, architectural roles,
cross-module dependencies, and entry points. Use this to quickly understand
how a codebase is organized — much faster than exploring directories manually.
Returns: module list with roles, entry points, dependency edges, and a Mermaid diagram.
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
                    scope = new
                    {
                        type = "string",
                        description = "Optional subdirectory to limit analysis (relative to project root)"
                    },
                    max_depth = new
                    {
                        type = "integer",
                        description = "Max folder depth for module detection (default 3)"
                    },
                    include_dependencies = new
                    {
                        type = "boolean",
                        description = "Include cross-module dependency edges and Mermaid diagram (default true)"
                    }
                },
                required = Array.Empty<string>()
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        string? scope = null;
        int maxDepth = 3;
        bool includeDependencies = true;

        if (!string.IsNullOrWhiteSpace(argumentsJson) && argumentsJson != "{}")
        {
            try
            {
                var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

                if (args.TryGetProperty("scope", out var scopeEl) && scopeEl.ValueKind == JsonValueKind.String)
                {
                    scope = scopeEl.GetString();
                }

                if (args.TryGetProperty("max_depth", out var depthEl) && depthEl.ValueKind == JsonValueKind.Number)
                {
                    maxDepth = depthEl.GetInt32();
                }

                if (args.TryGetProperty("include_dependencies", out var depsEl) && depsEl.ValueKind == JsonValueKind.True || depsEl.ValueKind == JsonValueKind.False)
                {
                    includeDependencies = depsEl.GetBoolean();
                }
            }
            catch
            {
                // Use defaults if JSON parsing fails
            }
        }

        return await _repoMapService.BuildRepoMapAsync(context.RootPath, scope, maxDepth, includeDependencies);
    }
}

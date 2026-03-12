using System.Text;
using System.Text.Json;
using AnswerCode.Services.Analysis;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Call graph tool — generates a static call graph starting from a given symbol,
/// showing what it calls (downstream) or what calls it (upstream).
/// Supports cycle detection, recursion handling, and confidence labels for
/// interface dispatch and unresolved dynamic calls.
/// </summary>
public class CallGraphTool : ITool
{
    public const string ToolName = "call_graph";

    private readonly ICallGraphService _callGraphService;

    public CallGraphTool(ICallGraphService callGraphService)
    {
        _callGraphService = callGraphService;
    }

    public string Name => ToolName;

    public string Description =>
"""
Generate a static call graph starting from a method/function.
Shows what the symbol calls (downstream) or what calls it (upstream).
Includes cycle detection, recursion markers, and confidence labels
(e.g. [interface dispatch], [unresolved]) for edges that cannot be
statically resolved with certainty.
Returns: tree view + edge list with file paths and line numbers.
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
                    symbol = new
                    {
                        type = "string",
                        description = "The method/function name to start the call graph from"
                    },
                    file_path = new
                    {
                        type = "string",
                        description = "Optional file path to disambiguate the symbol (relative to project root)"
                    },
                    depth = new
                    {
                        type = "integer",
                        description = "Max traversal depth (default 2, max 5)"
                    },
                    direction = new
                    {
                        type = "string",
                        description = "Direction: 'downstream' (what it calls) or 'upstream' (what calls it). Default: downstream",
                        @enum = new[] { "downstream", "upstream" }
                    }
                },
                required = new[] { "symbol" }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        string symbol;
        string? filePath = null;
        int depth = 2;
        string direction = "downstream";

        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

            if (!args.TryGetProperty("symbol", out var symbolEl) || string.IsNullOrWhiteSpace(symbolEl.GetString()))
            {
                return "Error: 'symbol' parameter is required.";
            }

            symbol = symbolEl.GetString()!;

            if (args.TryGetProperty("file_path", out var fpEl) && fpEl.ValueKind == JsonValueKind.String)
            {
                filePath = fpEl.GetString();
            }

            if (args.TryGetProperty("depth", out var depthEl) && depthEl.ValueKind == JsonValueKind.Number)
            {
                depth = depthEl.GetInt32();
            }

            if (args.TryGetProperty("direction", out var dirEl) && dirEl.ValueKind == JsonValueKind.String)
            {
                direction = dirEl.GetString() ?? "downstream";
            }
        }
        catch
        {
            return "Error: Invalid arguments JSON.";
        }

        var result = await _callGraphService.BuildCallGraphAsync(context.RootPath, symbol, filePath, depth, direction);

        return FormatResult(result);
    }

    private static string FormatResult(CallGraphResult result)
    {
        if (!result.IsSuccess)
        {
            return result.Message ?? $"No call graph could be built for '{result.Query}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Static Call Graph ({result.Direction}) for '{result.Query}' — depth {result.Depth}");
        sb.AppendLine();

        // Build adjacency for tree rendering
        if (result.Edges.Count > 0)
        {
            if (string.Equals(result.Direction, "downstream", StringComparison.OrdinalIgnoreCase))
            {
                RenderDownstreamTree(sb, result);
            }
            else
            {
                RenderUpstreamTree(sb, result);
            }

            // Edge list
            sb.AppendLine();
            sb.AppendLine($"Edges ({result.Edges.Count}):");

            foreach (var edge in result.Edges)
            {
                var label = string.IsNullOrEmpty(edge.Label) ? "" : $" [{edge.Label}]";
                var location = !string.IsNullOrEmpty(edge.Target.RelativePath)
                    ? $" ({edge.Target.RelativePath}:{edge.Target.Line})"
                    : "";
                sb.AppendLine($"  {edge.Source.Name} → {edge.Target.Name}{label}{location}");
            }
        }
        else
        {
            sb.AppendLine($"No {result.Direction} calls found for '{result.Query}'.");
        }

        // Warnings
        if (result.Warnings.Count > 0)
        {
            sb.AppendLine();
            foreach (var warning in result.Warnings.Distinct())
            {
                sb.AppendLine($"⚠ {warning}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void RenderDownstreamTree(StringBuilder sb, CallGraphResult result)
    {
        var root = result.Root!;
        sb.AppendLine($"{root.Name} ({FormatLocation(root)}, {root.Kind})");

        // Group edges by source for tree building
        var childrenBySource = result.Edges
            .GroupBy(e => GetNodeKey(e.Source))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var rootKey = GetNodeKey(root);

        if (childrenBySource.TryGetValue(rootKey, out var rootChildren))
        {
            RenderChildren(sb, rootChildren, childrenBySource, "", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static void RenderUpstreamTree(StringBuilder sb, CallGraphResult result)
    {
        var root = result.Root!;
        sb.AppendLine($"{root.Name} ({FormatLocation(root)}, {root.Kind}) ← called by:");

        // Group edges by target for upstream tree (callers → target)
        var callersByTarget = result.Edges
            .GroupBy(e => GetNodeKey(e.Target))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var rootKey = GetNodeKey(root);

        if (callersByTarget.TryGetValue(rootKey, out var rootCallers))
        {
            for (int i = 0; i < rootCallers.Count; i++)
            {
                var edge = rootCallers[i];
                var isLast = i == rootCallers.Count - 1;
                var prefix = isLast ? "└── " : "├── ";
                var label = string.IsNullOrEmpty(edge.Label) ? "" : $" [{edge.Label}]";

                sb.AppendLine($"{prefix}{edge.Source.Name}{label} ({FormatLocation(edge.Source)})");

                // Show callers of this caller
                var callerKey = GetNodeKey(edge.Source);
                if (callersByTarget.TryGetValue(callerKey, out var subCallers))
                {
                    var subPrefix = isLast ? "    " : "│   ";
                    for (int j = 0; j < subCallers.Count; j++)
                    {
                        var subEdge = subCallers[j];
                        var isSubLast = j == subCallers.Count - 1;
                        var subNodePrefix = isSubLast ? "└── " : "├── ";
                        var subLabel = string.IsNullOrEmpty(subEdge.Label) ? "" : $" [{subEdge.Label}]";
                        sb.AppendLine($"{subPrefix}{subNodePrefix}{subEdge.Source.Name}{subLabel} ({FormatLocation(subEdge.Source)})");
                    }
                }
            }
        }
    }

    private static void RenderChildren(StringBuilder sb,
                                        List<CallGraphEdge> children,
                                        Dictionary<string, List<CallGraphEdge>> childrenBySource,
                                        string indent,
                                        HashSet<string> rendered)
    {
        for (int i = 0; i < children.Count; i++)
        {
            var edge = children[i];
            var isLast = i == children.Count - 1;
            var prefix = isLast ? "└── " : "├── ";
            var label = string.IsNullOrEmpty(edge.Label) ? "" : $" [{edge.Label}]";
            var location = FormatLocation(edge.Target);

            sb.AppendLine($"{indent}{prefix}{edge.Target.Name}{label} ({location}, {edge.Target.Kind})");

            var targetKey = GetNodeKey(edge.Target);

            // Avoid infinite rendering for cycles
            if (!rendered.Add(targetKey))
            {
                continue;
            }

            if (childrenBySource.TryGetValue(targetKey, out var grandChildren) && grandChildren.Count > 0)
            {
                var childIndent = indent + (isLast ? "    " : "│   ");
                RenderChildren(sb, grandChildren, childrenBySource, childIndent, rendered);
            }
        }
    }

    private static string FormatLocation(CallGraphNode node)
    {
        if (string.IsNullOrEmpty(node.RelativePath))
        {
            return "external";
        }

        return $"{node.RelativePath}:{node.Line}";
    }

    private static string GetNodeKey(CallGraphNode node)
    {
        if (!string.IsNullOrEmpty(node.RelativePath))
        {
            return $"{node.RelativePath}:{node.Name}:{node.Line}";
        }

        return node.FullyQualifiedName;
    }
}

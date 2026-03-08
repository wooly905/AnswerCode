using System.Text;
using System.Text.Json;
using AnswerCode.Services.Analysis;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

public class FindReferencesTool(IReferenceAnalysisService referenceAnalysisService) : ITool
{
    public const string ToolName = "find_references";

    public string Name => ToolName;

    public string Description =>
        "Find references to a symbol across the repository. " +
        "Supports C#, JavaScript, TypeScript, Python, Java, Go, Rust, and C/C++. " +
        "C# uses Roslyn-aware resolution; the other supported languages use heuristic reference matching and classification.";

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
                        description = "Symbol name to search for references, such as 'ITool', 'ExecuteAsync', or 'AgentService'."
                    },
                    file_path = new
                    {
                        type = "string",
                        description = "Optional file path to narrow symbol resolution. Absolute or relative to the project root."
                    },
                    include = new
                    {
                        type = "string",
                        description = "Optional glob filter for result files, such as '*.cs' or 'Services/**/*.cs'."
                    },
                    scope = new
                    {
                        type = "string",
                        description = "Optional scope filter: 'all', 'tests', or 'production'. Default: 'all'."
                    },
                    signature_hint = new
                    {
                        type = "string",
                        description = "Optional extra hint such as containing type or parameter signature to disambiguate overloads."
                    }
                },
                required = new[] { "symbol" }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var symbol = args.GetProperty("symbol").GetString() ?? string.Empty;
        var filePath = args.TryGetProperty("file_path", out var filePathElement) ? filePathElement.GetString() : null;
        var include = args.TryGetProperty("include", out var includeElement) ? includeElement.GetString() : null;
        var scope = args.TryGetProperty("scope", out var scopeElement) ? scopeElement.GetString() : null;
        var signatureHint = args.TryGetProperty("signature_hint", out var signatureHintElement) ? signatureHintElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return "Error: symbol is required";
        }

        try
        {
            var result = await referenceAnalysisService.FindReferencesAsync(context.RootPath,
                                                                            symbol,
                                                                            filePath,
                                                                            include,
                                                                            scope,
                                                                            signatureHint);

            if (!result.IsSuccess)
            {
                return FormatFailure(symbol, result);
            }

            if (result.References.Count == 0)
            {
                return $"No references found for '{symbol}'.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Found {result.References.Count} reference(s) for '{symbol}':");
            sb.AppendLine($"Target: {result.Target!.Signature}");
            sb.AppendLine($"Declared at: {result.Target.RelativePath}:{result.Target.StartLine}-{result.Target.EndLine}");
            sb.AppendLine();

            foreach (var group in result.References.GroupBy(reference => reference.RelativePath))
            {
                sb.AppendLine($"{group.Key} ({group.Count()})");
                foreach (var reference in group.Take(12))
                {
                    sb.AppendLine($"  - line {reference.LineNumber} [{reference.Kind}] {reference.LineText}");
                }

                if (group.Count() > 12)
                {
                    sb.AppendLine($"  - ... {group.Count() - 12} more reference(s) in this file");
                }

                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error finding references: {ex.Message}";
        }
    }

    private static string FormatFailure(string symbol, ReferenceSearchResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            return result.Message;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Multiple symbol matches found for '{symbol}'. Refine with file_path or signature_hint.");
        sb.AppendLine();
        sb.AppendLine("Candidates:");

        foreach (var candidate in result.Candidates.Take(10))
        {
            sb.AppendLine($"- {candidate.RelativePath}:{candidate.StartLine} — {candidate.Signature}");
        }

        return sb.ToString().TrimEnd();
    }
}

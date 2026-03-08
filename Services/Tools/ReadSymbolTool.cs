using System.Text;
using System.Text.Json;
using AnswerCode.Services.Analysis;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

public class ReadSymbolTool(ISymbolAnalysisService symbolAnalysisService) : ITool
{
    public const string ToolName = "read_symbol";

    public string Name => ToolName;

    public string Description =>
        "Read a single symbol definition precisely instead of reading an entire file. " +
        "Supports C#, JavaScript, TypeScript, Python, Java, Go, Rust, and C/C++ symbols. " +
        "C# uses Roslyn for exact spans; the other supported languages use heuristic parsing. " +
        "Can include the body, leading comments, and use file_path or signature_hint to disambiguate overloads.";

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
                        description = "Symbol name to read, such as 'ExecuteAsync', 'AgentService', or 'ToolRegistry'."
                    },
                    file_path = new
                    {
                        type = "string",
                        description = "Optional file path to narrow symbol resolution. Absolute or relative to project root."
                    },
                    include_body = new
                    {
                        type = "boolean",
                        description = "Whether to include the full symbol body. Default: true."
                    },
                    include_comments = new
                    {
                        type = "boolean",
                        description = "Whether to include leading comments or XML docs. Default: true."
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
        var includeBody = !args.TryGetProperty("include_body", out var includeBodyElement) || includeBodyElement.GetBoolean();
        var includeComments = !args.TryGetProperty("include_comments", out var includeCommentsElement) || includeCommentsElement.GetBoolean();
        var signatureHint = args.TryGetProperty("signature_hint", out var signatureHintElement) ? signatureHintElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return "Error: symbol is required";
        }

        try
        {
            var resolved = await symbolAnalysisService.ResolveSymbolAsync(context.RootPath, symbol, filePath, signatureHint);
            if (!resolved.IsSuccess)
            {
                return FormatResolutionFailure(symbol, resolved);
            }

            var readResult = await symbolAnalysisService.ReadSymbolAsync(context.RootPath,
                                                                         symbol,
                                                                         filePath,
                                                                         includeBody,
                                                                         includeComments,
                                                                         signatureHint);

            if (readResult is null)
            {
                return $"Unable to read symbol '{symbol}'.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Symbol: {readResult.Match.Signature}");
            sb.AppendLine($"Kind: {readResult.Match.Kind}");
            sb.AppendLine($"File: {readResult.Match.RelativePath}");
            sb.AppendLine($"Declaration lines: {readResult.DeclarationStartLine}-{readResult.DeclarationEndLine}");
            sb.AppendLine($"Returned lines: {readResult.ContentStartLine}-{readResult.ContentEndLine}");
            sb.AppendLine($"Body included: {(readResult.IncludedBody ? "yes" : "no")}");
            sb.AppendLine($"Comments included: {(readResult.IncludedComments ? "yes" : "no")}");
            sb.AppendLine();
            sb.AppendLine(readResult.Content);
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error reading symbol: {ex.Message}";
        }
    }

    private static string FormatResolutionFailure(string symbol, ResolvedSymbolResult resolved)
    {
        if (!string.IsNullOrWhiteSpace(resolved.Message))
        {
            return resolved.Message;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Multiple symbol matches found for '{symbol}'. Refine with file_path or signature_hint.");
        sb.AppendLine();
        sb.AppendLine("Candidates:");

        foreach (var candidate in resolved.Candidates.Take(10))
        {
            sb.AppendLine($"- {candidate.RelativePath}:{candidate.StartLine} — {candidate.Signature}");
        }

        return sb.ToString().TrimEnd();
    }
}

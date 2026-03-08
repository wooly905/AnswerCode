using System.Text;
using System.Text.Json;
using AnswerCode.Services.Analysis;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

public class FindTestsTool(ITestDiscoveryService testDiscoveryService) : ITool
{
    public const string ToolName = "find_tests";

    public string Name => ToolName;

    public string Description =>
        "Find tests related to a source symbol or file. " +
        "Supports C#, JavaScript, TypeScript, Python, Java, Go, Rust, and C/C++. " +
        "Uses test naming conventions, framework markers, and direct symbol references to rank likely test files and cases.";

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
                        description = "Optional symbol to search for related tests, such as 'AgentService' or 'ExecuteAsync'."
                    },
                    file_path = new
                    {
                        type = "string",
                        description = "Optional source file path to infer related tests from its declared symbols. Absolute or relative to project root."
                    },
                    test_framework = new
                    {
                        type = "string",
                        description = "Optional test framework hint such as 'xUnit', 'NUnit', or 'MSTest'."
                    }
                }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var symbol = args.TryGetProperty("symbol", out var symbolElement) ? symbolElement.GetString() : null;
        var filePath = args.TryGetProperty("file_path", out var filePathElement) ? filePathElement.GetString() : null;
        var testFramework = args.TryGetProperty("test_framework", out var testFrameworkElement) ? testFrameworkElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: symbol or file_path is required";
        }

        try
        {
            var result = await testDiscoveryService.FindTestsAsync(context.RootPath, symbol, filePath, testFramework);
            if (!string.IsNullOrWhiteSpace(result.Message) && result.Matches.Count == 0)
            {
                if (result.Candidates.Count <= 1)
                {
                    return result.Message!;
                }

                var failure = new StringBuilder();
                failure.AppendLine(result.Message!);
                failure.AppendLine();
                failure.AppendLine("Candidates:");
                foreach (var candidate in result.Candidates.Take(10))
                {
                    failure.AppendLine($"- {candidate.RelativePath}:{candidate.StartLine} — {candidate.Signature}");
                }

                return failure.ToString().TrimEnd();
            }

            var query = symbol ?? filePath ?? string.Empty;
            var sb = new StringBuilder();
            sb.AppendLine($"Found {result.Matches.Count} test match(es) for '{query}':");

            if (result.Targets.Count > 0)
            {
                sb.AppendLine($"Targets: {string.Join(", ", result.Targets.Select(target => target.Signature))}");
            }

            sb.AppendLine();

            foreach (var match in result.Matches)
            {
                sb.AppendLine($"{match.RelativePath} [{match.Confidence}, {match.Framework}, score={match.Score}]");
                if (match.Reasons.Count > 0)
                {
                    sb.AppendLine($"  Reasons: {string.Join("; ", match.Reasons)}");
                }

                if (match.TestCases.Count > 0)
                {
                    sb.AppendLine("  Cases:");
                    foreach (var testCase in match.TestCases.Take(10))
                    {
                        sb.AppendLine($"    - line {testCase.LineNumber}: {testCase.Name} [{testCase.Confidence}] ({string.Join("; ", testCase.Reasons)})");
                    }
                }

                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error finding tests: {ex.Message}";
        }
    }
}

using System.Text;
using System.Text.Json;
using AnswerCode.Services.Analysis;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

public class ConfigLookupTool(IConfigLookupService configLookupService) : ITool
{
    public const string ToolName = "config_lookup";

    public string Name => ToolName;

    public string Description =>
        "Look up a configuration key across all config files in the project. " +
        "Finds where a config key is defined, its value in each source, and which value takes effect by precedence. " +
        "Supports C#, JavaScript, TypeScript, Python, Java, Go, Rust, and C/C++ config patterns.";

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
                    key = new
                    {
                        type = "string",
                        description = "The configuration key to search for. " +
                            "For hierarchical keys use ':' (e.g. 'Logging:LogLevel:Default') " +
                            "or '.' (e.g. 'spring.datasource.url'). " +
                            "Partial keys are supported — searching 'LogLevel' finds 'Logging:LogLevel:Default'."
                    }
                },
                required = new[] { "key" }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var key = args.TryGetProperty("key", out var keyElement) ? keyElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(key))
        {
            return "Error: 'key' parameter is required.";
        }

        try
        {
            var result = await configLookupService.LookupConfigKeyAsync(context.RootPath, key);

            if (!string.IsNullOrWhiteSpace(result.Message) && result.Entries.Count == 0)
            {
                return result.Message!;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Config lookup for '{result.Query}':");
            sb.AppendLine($"Detected languages: {string.Join(", ", result.DetectedLanguages)}");
            sb.AppendLine($"Config files scanned: {result.ConfigFiles.Count}");
            sb.AppendLine();

            if (result.Entries.Count == 0)
            {
                sb.AppendLine($"Key '{key}' was not found in any config file.");
                if (result.ConfigFiles.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Scanned files:");
                    foreach (var file in result.ConfigFiles)
                    {
                        sb.AppendLine($"  - {file.RelativePath} [{file.Format}]");
                    }
                }

                return sb.ToString().TrimEnd();
            }

            sb.AppendLine($"Found in {result.Entries.Count} location(s):");
            sb.AppendLine();

            foreach (var entry in result.Entries)
            {
                var lineInfo = entry.LineNumber > 0 ? $":line {entry.LineNumber}" : "";
                sb.AppendLine($"  {entry.RelativePath}{lineInfo} [{entry.Format}] precedence={entry.Precedence}");
                sb.AppendLine($"    {entry.Key} = {entry.Value}");
                sb.AppendLine();
            }

            if (result.EffectiveEntry is not null)
            {
                sb.AppendLine($"Effective value: {result.EffectiveEntry.Value}");
                sb.AppendLine($"  (from {result.EffectiveEntry.RelativePath}, precedence={result.EffectiveEntry.Precedence})");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error looking up config key: {ex.Message}";
        }
    }
}

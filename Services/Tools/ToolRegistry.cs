using System.Text;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Registry that holds all available tools and provides them as ChatTool definitions
/// </summary>
public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a tool
    /// </summary>
    public void Register(ITool tool) => _tools[tool.Name] = tool;

    /// <summary>
    /// Get a tool by name
    /// </summary>
    public ITool? GetTool(string name) => _tools.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>
    /// Get all registered tools
    /// </summary>
    public IReadOnlyList<ITool> GetAllTools() => _tools.Values.ToList();

    /// <summary>
    /// Get all tool definitions for the OpenAI ChatCompletionOptions
    /// </summary>
    public IReadOnlyList<ChatTool> GetChatToolDefinitions() => _tools.Values.Select(t => t.GetChatToolDefinition()).ToList();

    /// <summary>
    /// Generate text-based tool descriptions for the ReAct system prompt.
    /// Includes tool name, description, and JSON schema parameters.
    /// </summary>
    public string GetReActToolDescriptions()
    {
        var sb = new StringBuilder();

        foreach (var tool in _tools.Values)
        {
            var def = tool.GetChatToolDefinition();
            sb.AppendLine($"### {def.FunctionName}");
            sb.AppendLine($"Description: {def.FunctionDescription}");
            sb.AppendLine($"Parameters: {def.FunctionParameters}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

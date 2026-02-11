using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Interface for tools that can be called by the LLM agent
/// </summary>
public interface ITool
{
    /// <summary>
    /// Tool name (used as function name in tool calling)
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Tool description (tells the LLM when to use this tool)
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Get the ChatTool definition for OpenAI SDK tool calling
    /// </summary>
    ChatTool GetChatToolDefinition();

    /// <summary>
    /// Execute the tool with the given JSON arguments
    /// </summary>
    /// <param name="argumentsJson">JSON string of arguments from the LLM</param>
    /// <param name="context">Execution context (root path, etc.)</param>
    /// <returns>Text result to send back to the LLM</returns>
    Task<string> ExecuteAsync(string argumentsJson, ToolContext context);
}

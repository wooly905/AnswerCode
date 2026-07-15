using AnswerCode.Models;

namespace AnswerCode.Services;

/// <summary>
/// Agent Service Interface — runs an agentic tool-calling loop to answer code questions
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// Run the agent to answer a question about a codebase
    /// </summary>
    Task<AgentResult> RunAsync(string question,
                               string rootPath,
                               string? sessionId = null,
                               string? modelProvider = null,
                               string? userRole = null,
                               List<ConversationTurn>? conversationHistory = null);

    /// <summary>
    /// Run the agent with a progress callback for SSE streaming
    /// </summary>
    Task<AgentResult> RunAsync(string question,
                               string rootPath,
                               Func<AgentEvent, Task> onProgress,
                               string? sessionId = null,
                               string? modelProvider = null,
                               string? userRole = null,
                               List<ConversationTurn>? conversationHistory = null);
}

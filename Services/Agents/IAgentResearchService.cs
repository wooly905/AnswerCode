using AnswerCode.Models;
using AnswerCode.Services.Providers;

namespace AnswerCode.Services.Agents;

public interface IAgentResearchService
{
    Task<AgentResult> RunAsync(
        string question,
        string rootPath,
        ILLMProvider provider,
        Func<AgentEvent, Task> onProgress,
        string? userRole,
        string projectOverview,
        string? sessionId,
        int maxIterations,
        string? prefetchedContext);
}
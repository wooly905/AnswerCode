using AnswerCode.Models;
using AnswerCode.Services.AgentFramework;
using AnswerCode.Services.Providers;
using AnswerCode.Services.Tools;
using Microsoft.Extensions.AI;

namespace AnswerCode.Services.Agents;

public sealed class AgentResearchService(ILogger<AgentResearchService> logger,
                                         ToolRegistry toolRegistry,
                                         IUserInputService userInputService,
                                         IAnswerCodeChatClientFactory chatClientFactory,
                                         AnswerCodeAgentHarness agentHarness,
                                         AnswerCodeToolAdapter toolAdapter,
                                         ReActAgentRunner reActRunner) : IAgentResearchService
{
    public async Task<AgentResult> RunAsync(string question,
                                            string rootPath,
                                            ILLMProvider provider,
                                            Func<AgentEvent, Task> onProgress,
                                            AnswerRole userRole,
                                            string projectOverview,
                                            string? sessionId,
                                            int maxIterations,
                                            string? prefetchedContext)
    {
        if (!provider.SupportsToolCalling)
        {
            logger.LogInformation("Provider {Provider} does not support native tool calling, using ReAct agent loop", provider.Name);
            return await reActRunner.RunAsync(question,
                                              rootPath,
                                              provider,
                                              onProgress,
                                              userRole,
                                              projectOverview,
                                              sessionId,
                                              maxIterations,
                                              prefetchedContext).ConfigureAwait(false);
        }

        logger.LogInformation("Running Microsoft Agent Framework Harness for provider {Provider}", provider.Name);
        var toolContext = new ToolContext
        {
            RootPath = rootPath,
            Logger = logger,
            OnProgress = onProgress,
            SessionId = sessionId,
            UserInputService = userInputService
        };
        IReadOnlyList<AITool> tools = toolAdapter.Adapt(toolRegistry.GetAllTools(), toolContext);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, $"## Project Overview\n{projectOverview}")
        };
        if (!string.IsNullOrWhiteSpace(prefetchedContext))
        {
            messages.Add(new ChatMessage(ChatRole.User, $"## Pre-fetched Symbol Context (verified against the codebase; still confirm with tools before relying on it)\n{prefetchedContext}"));
        }

        messages.Add(new ChatMessage(ChatRole.User, $"## Question\n{question}"));
        await onProgress(new AgentEvent
        {
            Type = AgentEventType.SubAgentThinking,
            Iteration = 1,
            Thinking = "Analyzing question and planning approach..."
        }).ConfigureAwait(false);

        return await agentHarness.RunAsync(chatClientFactory.Create(provider.Name),
                                           AgentPromptCatalog.ForRole(userRole),
                                           messages,
                                           tools,
                                           maxIterations,
                                           rootPath,
                                           onProgress).ConfigureAwait(false);
    }
}
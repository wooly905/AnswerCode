using System.Diagnostics;
using AnswerCode.Models;

namespace AnswerCode.Services.Questions;

public sealed class QuestionExecutionService(
    IAgentService agentService,
    IConversationHistoryService conversationHistory,
    ILogger<QuestionExecutionService> logger) : IQuestionExecutionService
{
    public async Task<AnswerResponse> ExecuteAsync(
        QuestionRequest request,
        string projectPath,
        Func<AgentEvent, Task>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sessionId = request.SessionId ?? Guid.NewGuid().ToString();
        List<ConversationTurn> history = conversationHistory.GetHistory(sessionId);
        AnswerRole userRole = request.UserRole ?? AnswerRole.Developer;
        var stopwatch = Stopwatch.StartNew();

        AgentResult result = onProgress is null
            ? await agentService.RunAsync(request.Question,
                                          projectPath,
                                          sessionId,
                                          request.ModelProvider,
                                          userRole,
                                          history)
            : await agentService.RunAsync(request.Question,
                                          projectPath,
                                          onProgress,
                                          sessionId,
                                          request.ModelProvider,
                                          userRole,
                                          history);

        conversationHistory.AddTurn(sessionId, new ConversationTurn { Role = "user", Content = request.Question });
        conversationHistory.AddTurn(sessionId, new ConversationTurn { Role = "assistant", Content = result.Answer });

        logger.LogInformation("Question answered in {ElapsedMs}ms ({ToolCalls} tool calls, {Iterations} iterations)",
                              stopwatch.ElapsedMilliseconds,
                              result.TotalToolCalls,
                              result.IterationCount);

        return new AnswerResponse
        {
            Answer = result.Answer,
            ThinkingContent = result.ThinkingContent,
            RelevantFiles = result.RelevantFiles,
            ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
            SessionId = sessionId,
            ToolCallCount = result.TotalToolCalls,
            IterationCount = result.IterationCount,
            ToolCalls = result.ToolCalls,
            TotalInputTokens = result.TotalInputTokens,
            TotalOutputTokens = result.TotalOutputTokens,
            MainAgentInputTokens = result.MainAgentInputTokens,
            MainAgentOutputTokens = result.MainAgentOutputTokens
        };
    }
}

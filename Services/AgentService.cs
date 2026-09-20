using AnswerCode.Models;
using AnswerCode.Services.Agents;
using AnswerCode.Services.Tools;
using Microsoft.Extensions.Options;

namespace AnswerCode.Services;

/// <summary>
/// Agent Service — runs an agentic tool-calling loop where the LLM autonomously decides
/// which tools to use, when to search, and when to stop.
///
/// When conversation history exists, uses a SubAgent architecture to save tokens:
///   Phase 1: Resolve follow-up question into standalone question (with history, 1 LLM call)
///   Phase 2: SubAgent tool loop — full agentic research (without history, N LLM calls)
///   Phase 3: Synthesize final answer (with history + research findings, 1 LLM call)
/// </summary>
public class AgentService(ILogger<AgentService> logger,
                          ILLMServiceFactory llmFactory,
                          IContextExpansionService contextExpansionService,
                          IConversationContextService conversationContextService,
                          IAgentResearchService researchService,
                          IOptions<AgentSettings> agentSettingsOptions) : IAgentService
{
    private readonly AgentSettings _settings = agentSettingsOptions.Value;

    /// <summary>
    /// Run without progress callback (original API)
    /// </summary>
    public Task<AgentResult> RunAsync(string question,
                                      string rootPath,
                                      string? sessionId = null,
                                      string? modelProvider = null,
                                      AnswerRole userRole = AnswerRole.Developer,
                                      List<ConversationTurn>? conversationHistory = null)
    {
        return RunAsync(question, rootPath, _ => Task.CompletedTask, sessionId, modelProvider, userRole, conversationHistory);
    }

    /// <summary>
    /// Main orchestrator — uses SubAgent architecture when conversation history exists.
    /// When no history, runs the tool loop directly with zero overhead.
    /// </summary>
    public async Task<AgentResult> RunAsync(string question,
                                            string rootPath,
                                            Func<AgentEvent, Task> onProgress,
                                            string? sessionId = null,
                                            string? modelProvider = null,
                                            AnswerRole userRole = AnswerRole.Developer,
                                            List<ConversationTurn>? conversationHistory = null)
    {
        logger.LogInformation("Agent starting for question: {Question}, project: {RootPath}", question, rootPath);

        var provider = llmFactory.GetProvider(modelProvider);
        await onProgress(new AgentEvent { Type = AgentEventType.Started });

        var projectOverview = ProjectOverviewBuilder.Build(rootPath);
        bool hasHistory = conversationHistory is { Count: > 0 };

        if (!hasHistory)
        {
            // No history — run tool loop directly (zero overhead, same behavior as before)
            var noHistoryComplexity = _settings.EnableComplexityRouting
                ? QuestionComplexityClassifier.Classify(question)
                : QuestionComplexity.Complex;
            int noHistoryMaxIterations = GetIterationBudget(noHistoryComplexity);
            string? noHistorySymbolContext = _settings.EnableSymbolContextExpansion
                ? await contextExpansionService.BuildSymbolContextAsync(rootPath, question)
                : null;

            AgentResult noHistoryResult = await researchService.RunAsync(
                question, rootPath, provider, onProgress, userRole, projectOverview,
                sessionId, noHistoryMaxIterations, noHistorySymbolContext);

            noHistoryResult.ComplexityLabel = noHistoryComplexity.ToString();
            noHistoryResult.UsedPrefetchedContext = !string.IsNullOrWhiteSpace(noHistorySymbolContext);
            noHistoryResult.MainAgentInputTokens = noHistoryResult.TotalInputTokens;
            noHistoryResult.MainAgentOutputTokens = noHistoryResult.TotalOutputTokens;
            return noHistoryResult;
        }

        // === SubAgent architecture: 3 phases ===

        // Phase 0: Check history token budget and compress if needed
        conversationHistory = await conversationContextService.PrepareHistoryAsync(
            provider,
            conversationHistory!,
            sessionId);

        // Phase 1: Resolve follow-up question into standalone question
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseStart, Phase = 1, PhaseLabel = "Context Resolution" });
        logger.LogInformation("SubAgent Phase 1: Resolving follow-up question with {Turns} history turns", conversationHistory.Count);
        string resolvedQuestion;
        int p1InputTokens = 0, p1OutputTokens = 0;
        try
        {
            (resolvedQuestion, p1InputTokens, p1OutputTokens) = await conversationContextService.ResolveQuestionAsync(
                provider, question, conversationHistory);
            logger.LogInformation("SubAgent Phase 1 complete. Resolved: {Resolved}", resolvedQuestion);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Phase 1 (context resolution) failed, falling back to original question");
            resolvedQuestion = question;
        }
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseEnd, Phase = 1, PhaseLabel = "Context Resolution", ResolvedQuestion = resolvedQuestion });

        // Phase 2: SubAgent tool loop (no history — the core token saving)
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseStart, Phase = 2, PhaseLabel = "SubAgent Research" });
        logger.LogInformation("SubAgent Phase 2: Running tool loop with resolved question (no history)");

        var phase2Complexity = _settings.EnableComplexityRouting
            ? QuestionComplexityClassifier.Classify(resolvedQuestion)
            : QuestionComplexity.Complex;
        int phase2MaxIterations = GetIterationBudget(phase2Complexity);
        string? phase2SymbolContext = _settings.EnableSymbolContextExpansion
            ? await contextExpansionService.BuildSymbolContextAsync(rootPath, resolvedQuestion)
            : null;

        AgentResult result = await researchService.RunAsync(
            resolvedQuestion, rootPath, provider, onProgress, userRole, projectOverview,
            sessionId, phase2MaxIterations, phase2SymbolContext);
        result.ComplexityLabel = phase2Complexity.ToString();
        result.UsedPrefetchedContext = !string.IsNullOrWhiteSpace(phase2SymbolContext);
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseEnd, Phase = 2, PhaseLabel = "SubAgent Research", Summary = $"{result.TotalToolCalls} tool calls, {result.IterationCount} iterations" });

        // Phase 3: Synthesize final answer with conversation context + research findings
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseStart, Phase = 3, PhaseLabel = "Answer Synthesis" });
        logger.LogInformation("SubAgent Phase 3: Synthesizing answer with conversation context");
        int p3InputTokens = 0, p3OutputTokens = 0;
        try
        {
            var cleanedFindings = ReActParser.CleanNativeTokens(result.Answer);
            var (finalAnswer, p3In, p3Out) = await conversationContextService.SynthesizeAnswerAsync(
                provider, question, conversationHistory, cleanedFindings, userRole);
            result.Answer = ReActParser.CleanNativeTokens(finalAnswer);
            p3InputTokens = p3In;
            p3OutputTokens = p3Out;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Phase 3 (synthesis) failed, using SubAgent answer directly");
        }
        await onProgress(new AgentEvent { Type = AgentEventType.PhaseEnd, Phase = 3, PhaseLabel = "Answer Synthesis" });

        // Track main agent tokens (Phase 1 + Phase 3) separately from subagent (Phase 2)
        result.MainAgentInputTokens = p1InputTokens + p3InputTokens;
        result.MainAgentOutputTokens = p1OutputTokens + p3OutputTokens;
        result.TotalInputTokens += result.MainAgentInputTokens;
        result.TotalOutputTokens += result.MainAgentOutputTokens;

        return result;
    }

    /// <summary>
    /// Resolve the tool-loop iteration budget for a given complexity classification,
    /// based on configured limits in <see cref="AgentSettings"/>.
    /// </summary>
    private int GetIterationBudget(QuestionComplexity complexity) => complexity switch
    {
        QuestionComplexity.Simple => _settings.SimpleQuestionMaxIterations,
        QuestionComplexity.Complex => _settings.ComplexQuestionMaxIterations,
        _ => _settings.StandardQuestionMaxIterations
    };

}

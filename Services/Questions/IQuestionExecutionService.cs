using AnswerCode.Models;

namespace AnswerCode.Services.Questions;

public interface IQuestionExecutionService
{
    Task<AnswerResponse> ExecuteAsync(
        QuestionRequest request,
        string projectPath,
        Func<AgentEvent, Task>? onProgress = null,
        CancellationToken cancellationToken = default);
}
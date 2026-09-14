namespace AnswerCode.Models;

public sealed class UserAnswerRequest
{
    public string QuestionId { get; set; } = string.Empty;

    public string? Answer { get; set; }
}
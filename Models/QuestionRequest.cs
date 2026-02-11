namespace AnswerCode.Models;

/// <summary>
/// Question request
/// </summary>
public class QuestionRequest
{
    /// <summary>
    /// Project path
    /// </summary>
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>
    /// User question
    /// </summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>
    /// Session ID (for maintaining conversation context)
    /// </summary>
    public string? SessionId
    {
        get; set;
    }

    /// <summary>
    /// Model provider selection (OpenAI, AzureOpenAI)
    /// </summary>
    public string? ModelProvider { get; set; }
}

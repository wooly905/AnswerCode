namespace AnswerCode.Models;

/// <summary>
/// Search request
/// </summary>
public class SearchRequest
{
    public string ProjectPath { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public bool CaseInsensitive { get; set; } = true;
    public string? Include { get; set; }
}

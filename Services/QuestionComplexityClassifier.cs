using System.Text.RegularExpressions;

namespace AnswerCode.Services;

/// <summary>
/// Complexity classification used to size the tool-loop iteration budget for a question.
/// </summary>
public enum QuestionComplexity
{
    /// <summary>Single-fact lookup (e.g. "where is X defined") — small iteration budget.</summary>
    Simple,
    /// <summary>Default/ambiguous — moderate iteration budget.</summary>
    Standard,
    /// <summary>Multi-hop / architectural / "how does X work" — full iteration budget.</summary>
    Complex
}

/// <summary>
/// Cheap, rule-based classifier that estimates how much exploration a question likely needs,
/// without spending an extra LLM round-trip. Used to size the tool-loop iteration budget.
///
/// Ambiguous cases never classify as Simple — they fall back to Standard — so misclassification
/// can only make a run use more iterations than strictly necessary, never truncate a hard
/// question prematurely.
/// </summary>
public static class QuestionComplexityClassifier
{
    private static readonly Regex _simplePattern = new(@"^\s*(where is|what is|which file|what does|show me the definition of|find the definition of|哪個檔案|在哪|是什麼|定義在哪)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _complexKeywords = new(@"how does .* work|trace|end[- ]to[- ]end|explain the flow|what happens when|impact of|architecture|walk me through|流程|架構|為什麼", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex _identifierPattern = new(@"\b[A-Za-z_][a-z0-9]*[A-Z][A-Za-z0-9_]*\b", RegexOptions.Compiled);

    public static QuestionComplexity Classify(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return QuestionComplexity.Standard;
        }

        int wordCount = question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int identifierCount = _identifierPattern.Matches(question).Count;

        if (_complexKeywords.IsMatch(question) || wordCount > 40 || identifierCount >= 3)
        {
            return QuestionComplexity.Complex;
        }

        if (_simplePattern.IsMatch(question) && wordCount < 20 && identifierCount <= 1)
        {
            return QuestionComplexity.Simple;
        }

        return QuestionComplexity.Standard;
    }
}

using AnswerCode.Services;

namespace AnswerCode.Tests.Services;

public class QuestionComplexityClassifierTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Can you review this method?")]
    public void Classify_AmbiguousQuestion_ReturnsStandard(string? question)
    {
        var result = QuestionComplexityClassifier.Classify(question!);

        Assert.Equal(QuestionComplexity.Standard, result);
    }

    [Theory]
    [InlineData("Where is UserStorageService defined?")]
    [InlineData("What does RepoMapService do?")]
    [InlineData("哪個檔案 定義 UserStorageService")]
    public void Classify_ShortLookupQuestion_ReturnsSimple(string question)
    {
        var result = QuestionComplexityClassifier.Classify(question);

        Assert.Equal(QuestionComplexity.Simple, result);
    }

    [Theory]
    [InlineData("How does the authentication flow work?")]
    [InlineData("Explain the architecture of this application")]
    [InlineData("請解釋登入流程為什麼需要兩個 token")]
    public void Classify_ArchitecturalQuestion_ReturnsComplex(string question)
    {
        var result = QuestionComplexityClassifier.Classify(question);

        Assert.Equal(QuestionComplexity.Complex, result);
    }

    [Fact]
    public void Classify_QuestionWithSeveralIdentifiers_ReturnsComplex()
    {
        var result = QuestionComplexityClassifier.Classify(
            "How are AgentService, ToolRegistry, and ConversationHistoryService connected?");

        Assert.Equal(QuestionComplexity.Complex, result);
    }

    [Fact]
    public void Classify_LongQuestion_ReturnsComplex()
    {
        var question = string.Join(' ', Enumerable.Repeat("detail", 41));

        var result = QuestionComplexityClassifier.Classify(question);

        Assert.Equal(QuestionComplexity.Complex, result);
    }
}
namespace AnswerCode.Services.Analysis;

public interface ICSharpCompilationService
{
    Task<CSharpCompilationContext> GetCompilationAsync(string rootPath, CancellationToken cancellationToken = default);
}

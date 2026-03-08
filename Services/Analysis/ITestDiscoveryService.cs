namespace AnswerCode.Services.Analysis;

public interface ITestDiscoveryService
{
    Task<TestDiscoveryResult> FindTestsAsync(string rootPath,
                                             string? symbol = null,
                                             string? filePath = null,
                                             string? testFramework = null,
                                             CancellationToken cancellationToken = default);
}

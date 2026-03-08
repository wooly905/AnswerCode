namespace AnswerCode.Services.Analysis;

public interface ILanguageHeuristicService
{
    Task<IReadOnlyList<SourceSymbolMatch>> FindDefinitionsAsync(string rootPath,
                                                                string symbol,
                                                                string? filePath = null,
                                                                string? signatureHint = null,
                                                                CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SourceSymbolMatch>> GetDeclaredSymbolsInFileAsync(string rootPath,
                                                                         string filePath,
                                                                         CancellationToken cancellationToken = default);

    Task<SymbolReadResult?> ReadSymbolAsync(SourceSymbolMatch match,
                                            bool includeBody,
                                            bool includeComments,
                                            CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SymbolReferenceMatch>> FindReferencesAsync(string rootPath,
                                                                  SourceSymbolMatch target,
                                                                  string? include = null,
                                                                  string? scope = null,
                                                                  CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TestFileMatch>> FindTestsAsync(string rootPath,
                                                      IReadOnlyList<SourceSymbolMatch> targets,
                                                      string? testFramework = null,
                                                      CancellationToken cancellationToken = default);
}

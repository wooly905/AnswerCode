namespace AnswerCode.Services.Analysis;

public interface ISymbolAnalysisService
{
    Task<ResolvedSymbolResult> ResolveSymbolAsync(string rootPath,
                                                  string symbol,
                                                  string? filePath = null,
                                                  string? signatureHint = null,
                                                  CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SourceSymbolMatch>> FindDefinitionsAsync(string rootPath,
                                                                string symbol,
                                                                string? filePath = null,
                                                                string? signatureHint = null,
                                                                CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SourceSymbolMatch>> GetDeclaredSymbolsInFileAsync(string rootPath,
                                                                         string filePath,
                                                                         CancellationToken cancellationToken = default);

    Task<SymbolReadResult?> ReadSymbolAsync(string rootPath,
                                            string symbol,
                                            string? filePath,
                                            bool includeBody,
                                            bool includeComments,
                                            string? signatureHint = null,
                                            CancellationToken cancellationToken = default);
}

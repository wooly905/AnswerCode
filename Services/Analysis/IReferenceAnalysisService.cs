namespace AnswerCode.Services.Analysis;

public interface IReferenceAnalysisService
{
    Task<ReferenceSearchResult> FindReferencesAsync(string rootPath,
                                                    string symbol,
                                                    string? filePath = null,
                                                    string? include = null,
                                                    string? scope = null,
                                                    string? signatureHint = null,
                                                    CancellationToken cancellationToken = default);
}

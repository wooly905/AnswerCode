namespace AnswerCode.Services.Analysis;

public interface IConfigLookupService
{
    Task<ConfigLookupResult> LookupConfigKeyAsync(string rootPath,
                                                  string key,
                                                  CancellationToken cancellationToken = default);
}

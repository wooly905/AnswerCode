using Microsoft.AspNetCore.Http;

namespace AnswerCode.Services.Uploads;

public interface ISourceFilePolicy
{
    bool IsAllowed(string relativePath);

    string? ResolveDestinationPath(string destinationRoot, string relativePath);

    Task<bool> IsSafeContentAsync(IFormFile file, CancellationToken cancellationToken = default);
}
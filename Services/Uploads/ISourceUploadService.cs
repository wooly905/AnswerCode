using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace AnswerCode.Services.Uploads;

public interface ISourceUploadService
{
    Task<SourceUploadResult> UploadAsync(
        IReadOnlyList<IFormFile> files,
        IReadOnlyList<string>? relativePaths,
        string? folderId,
        string? projectName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    SourceDeleteResult Delete(string folderId, string? deleteToken, ClaimsPrincipal user);

    IReadOnlyList<SourceProjectSummary> ListAnonymousUploads();

    IReadOnlyList<SourceProjectSummary> ListUserProjects(ClaimsPrincipal user);
}
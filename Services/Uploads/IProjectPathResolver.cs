using System.Security.Claims;

namespace AnswerCode.Services.Uploads;

public interface IProjectPathResolver
{
    bool IsPathAllowed(string path, ClaimsPrincipal user);

    string ResolveFolderPath(string folderId, ClaimsPrincipal user);

    string ResolveProjectPath(string? input, ClaimsPrincipal user);
}
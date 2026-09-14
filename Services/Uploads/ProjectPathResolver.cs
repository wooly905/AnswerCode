using System.Security.Claims;

namespace AnswerCode.Services.Uploads;

public sealed class ProjectPathResolver(
    IWebHostEnvironment environment,
    IUserStorageService userStorage,
    ILogger<ProjectPathResolver> logger) : IProjectPathResolver
{
    public bool IsPathAllowed(string path, ClaimsPrincipal user)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        string anonymousRoot = Path.GetFullPath(Path.Combine(environment.WebRootPath, "source-code"));
        if (IsWithin(fullPath, anonymousRoot))
        {
            return true;
        }

        return user.Identity?.IsAuthenticated == true
            && IsWithin(fullPath, Path.GetFullPath(userStorage.GetUserStoragePath(user)));
    }

    public string ResolveFolderPath(string folderId, ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated == true)
        {
            string userCandidate = Path.Combine(userStorage.GetUserStoragePath(user), folderId);
            if (Directory.Exists(userCandidate))
            {
                return userCandidate;
            }
        }

        return Path.Combine(environment.WebRootPath, "source-code", folderId);
    }

    public string ResolveProjectPath(string? input, ClaimsPrincipal user)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        if (!input.Contains('/') && !input.Contains('\\') && !input.Contains(':'))
        {
            string folderPath = ResolveFolderPath(input, user);
            if (Directory.Exists(folderPath))
            {
                return folderPath;
            }
        }

        string resolved = Path.IsPathRooted(input)
            ? Path.GetFullPath(input)
            : Path.GetFullPath(Path.Combine(environment.WebRootPath, "source-code", input));
        if (IsPathAllowed(resolved, user))
        {
            return resolved;
        }

        logger.LogWarning("Blocked path traversal attempt: {Input}", input);
        return string.Empty;
    }

    private static bool IsWithin(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
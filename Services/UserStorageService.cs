using System.Security.Claims;

namespace AnswerCode.Services;

public interface IUserStorageService
{
    string GetUserStoragePath(ClaimsPrincipal user);
    double GetUsageMB(ClaimsPrincipal user);
    bool CheckQuota(ClaimsPrincipal user, long additionalBytes);
    int GetMaxSizeMB();
}

public class UserStorageService : IUserStorageService
{
    private readonly IWebHostEnvironment _env;
    private readonly int _maxSizeMB;

    public UserStorageService(IWebHostEnvironment env, IConfiguration configuration)
    {
        _env = env;
        _maxSizeMB = configuration.GetValue("UserStorage:MaxSizeMB", 300);
    }

    private static string GetUserDirectoryName(ClaimsPrincipal user)
    {
        var email = user.FindFirst(ClaimTypes.Email)?.Value?.Trim().ToLowerInvariant();
        
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("User email claim not found");
        }

        if (email is "." or ".."
            || email.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || email.Contains(Path.DirectorySeparatorChar)
            || email.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("User email claim is not a valid directory name");
        }

        return email;
    }

    public string GetUserStoragePath(ClaimsPrincipal user)
    {
        var directoryName = GetUserDirectoryName(user);
        var path = Path.Combine(_env.WebRootPath, "source-code", "users", directoryName);
        Directory.CreateDirectory(path);
        return path;
    }

    public double GetUsageMB(ClaimsPrincipal user)
    {
        var storagePath = GetUserStoragePath(user);
        if (!Directory.Exists(storagePath))
            return 0;

        var dirInfo = new DirectoryInfo(storagePath);
        var totalBytes = dirInfo.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return totalBytes / (1024.0 * 1024.0);
    }

    public bool CheckQuota(ClaimsPrincipal user, long additionalBytes)
    {
        var usageMB = GetUsageMB(user);
        var additionalMB = additionalBytes / (1024.0 * 1024.0);
        return (usageMB + additionalMB) <= _maxSizeMB;
    }

    public int GetMaxSizeMB() => _maxSizeMB;
}

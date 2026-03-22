using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace AnswerCode.Services;

public interface IUserStorageService
{
    string GetUserHashedId(ClaimsPrincipal user);
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

    public string GetUserHashedId(ClaimsPrincipal user)
    {
        var email = user.FindFirst(ClaimTypes.Email)?.Value?.ToLowerInvariant();
        if (string.IsNullOrEmpty(email))
            throw new InvalidOperationException("User email claim not found");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(email));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public string GetUserStoragePath(ClaimsPrincipal user)
    {
        var hashedId = GetUserHashedId(user);
        var path = Path.Combine(_env.WebRootPath, "source-code", "users", hashedId);
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

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

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
    private readonly object _storageLock = new();

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
        var usersPath = Path.Combine(_env.WebRootPath, "source-code", "users");
        var path = Path.Combine(usersPath, directoryName);

        lock (_storageLock)
        {
            MigrateLegacyDirectory(usersPath, directoryName, path);
            Directory.CreateDirectory(path);
        }

        return path;
    }

    private static void MigrateLegacyDirectory(string usersPath, string email, string destinationPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(email));
        var legacyPath = Path.Combine(usersPath, Convert.ToHexString(hash)[..16].ToLowerInvariant());
        if (!Directory.Exists(legacyPath))
        {
            return;
        }

        if (!Directory.Exists(destinationPath))
        {
            Directory.Move(legacyPath, destinationPath);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(legacyPath))
        {
            var destination = Path.Combine(destinationPath, Path.GetFileName(entry));
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                Directory.Move(entry, destination);
            }
            else
            {
                File.Move(entry, destination);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(legacyPath).Any())
        {
            Directory.Delete(legacyPath);
        }
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

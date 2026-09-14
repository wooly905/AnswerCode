using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AnswerCode.Services.Uploads;

public sealed class SourceUploadService(
    IWebHostEnvironment environment,
    IUserStorageService userStorage,
    ISourceFilePolicy filePolicy,
    IDeleteTokenService deleteTokenService,
    ILogger<SourceUploadService> logger) : ISourceUploadService
{
    public const long AnonymousMaxUploadBytes = 20 * 1024 * 1024;
    public const long AuthenticatedMaxUploadBytes = 300 * 1024 * 1024;

    public async Task<SourceUploadResult> UploadAsync(
        IReadOnlyList<IFormFile> files,
        IReadOnlyList<string>? relativePaths,
        string? folderId,
        string? projectName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            throw new SourceUploadException("No files provided");
        }

        bool persistent = user.Identity?.IsAuthenticated == true;
        long totalSize = files.Sum(file => file.Length);
        long sizeLimit = persistent ? AuthenticatedMaxUploadBytes : AnonymousMaxUploadBytes;
        if (totalSize > sizeLimit)
        {
            throw new SourceUploadException(
                $"Total upload size ({totalSize / (1024.0 * 1024.0):F2} MB) exceeds the {sizeLimit / (1024.0 * 1024.0):F0} MB limit.");
        }

        if (persistent && !userStorage.CheckQuota(user, totalSize))
        {
            throw new SourceUploadException(
                $"Storage quota exceeded. Used: {userStorage.GetUsageMB(user):F1} MB / {userStorage.GetMaxSizeMB()} MB. Please delete some projects first.");
        }

        folderId = string.IsNullOrWhiteSpace(folderId) ? Guid.NewGuid().ToString("N")[..12] : folderId;
        if (!IsValidFolderId(folderId))
        {
            throw new SourceUploadException("Invalid folder ID");
        }

        string destinationRoot = persistent
            ? Path.Combine(userStorage.GetUserStoragePath(user), folderId)
            : Path.Combine(environment.WebRootPath, "source-code", folderId);
        Directory.CreateDirectory(destinationRoot);

        logger.LogInformation("Uploading {Count} files ({Size} bytes) to {Destination}", files.Count, totalSize, destinationRoot);
        for (int index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IFormFile file = files[index];
            string relativePath = relativePaths is not null
                && index < relativePaths.Count
                && !string.IsNullOrWhiteSpace(relativePaths[index])
                    ? relativePaths[index]
                    : file.FileName;
            string? destinationPath = filePolicy.ResolveDestinationPath(destinationRoot, relativePath);
            if (destinationPath is null || !filePolicy.IsAllowed(relativePath))
            {
                logger.LogWarning("Skipping disallowed or unsafe upload path: {Path}", relativePath);
                continue;
            }

            if (!await filePolicy.IsSafeContentAsync(file, cancellationToken).ConfigureAwait(false))
            {
                logger.LogWarning("Blocking executable binary signature in uploaded file: {Path}", relativePath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var stream = new FileStream(destinationPath, FileMode.Create);
            await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        FileInfo[] storedFiles = new DirectoryInfo(destinationRoot)
            .GetFiles("*", SearchOption.AllDirectories);
        string displayName = !string.IsNullOrWhiteSpace(projectName)
            ? projectName.Trim()
            : DetectProjectName(relativePaths);
        await File.WriteAllTextAsync(
            Path.Combine(destinationRoot, ".project-meta.json"),
            JsonSerializer.Serialize(new { displayName, createdAt = DateTime.UtcNow }, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        long totalStoredBytes = storedFiles.Sum(file => file.Length);
        return new SourceUploadResult(
            folderId,
            displayName,
            storedFiles.Length,
            totalStoredBytes,
            Math.Round(totalStoredBytes / (1024.0 * 1024.0), 2),
            persistent,
            persistent ? null : deleteTokenService.Issue(folderId));
    }

    public SourceDeleteResult Delete(string folderId, string? deleteToken, ClaimsPrincipal user)
    {
        if (!IsValidFolderId(folderId))
        {
            return new(SourceDeleteStatus.InvalidFolderId, "Invalid folder ID", folderId);
        }

        bool authenticated = user.Identity?.IsAuthenticated == true;
        if (!authenticated && !deleteTokenService.Validate(folderId, deleteToken))
        {
            return new(SourceDeleteStatus.Unauthorized, "Invalid or missing delete token", folderId);
        }

        string folderPath = authenticated
            ? Path.Combine(userStorage.GetUserStoragePath(user), folderId)
            : Path.Combine(environment.WebRootPath, "source-code", folderId);
        if (!Directory.Exists(folderPath))
        {
            return new(SourceDeleteStatus.NotFound, $"Folder not found: {folderId}", folderId);
        }

        try
        {
            Directory.Delete(folderPath, recursive: true);
            logger.LogInformation("Deleted source-code folder: {Id}", folderId);
            return new(SourceDeleteStatus.Success, "Source code removed", folderId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting source-code folder {Id}", folderId);
            return new(SourceDeleteStatus.Failed, "Failed to delete source code folder", folderId);
        }
    }

    public IReadOnlyList<SourceProjectSummary> ListAnonymousUploads() =>
        ListProjects(Path.Combine(environment.WebRootPath, "source-code"));

    public IReadOnlyList<SourceProjectSummary> ListUserProjects(ClaimsPrincipal user) =>
        ListProjects(userStorage.GetUserStoragePath(user));

    private static IReadOnlyList<SourceProjectSummary> ListProjects(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.GetDirectories(root)
            .Select(path => new DirectoryInfo(path))
            .Select(directory =>
            {
                FileInfo[] files = directory.GetFiles("*", SearchOption.AllDirectories)
                    .Where(file => file.Name != ".project-meta.json")
                    .ToArray();
                return new SourceProjectSummary(
                    directory.Name,
                    ReadDisplayName(directory.FullName) ?? directory.Name,
                    files.Length,
                    Math.Round(files.Sum(file => file.Length) / (1024.0 * 1024.0), 2),
                    directory.CreationTimeUtc);
            })
            .OrderByDescending(project => project.CreatedAt)
            .ToList();
    }

    private static bool IsValidFolderId(string folderId) =>
        !string.IsNullOrWhiteSpace(folderId)
        && !folderId.Contains("..", StringComparison.Ordinal)
        && !folderId.Contains('/')
        && !folderId.Contains('\\')
        && !folderId.Contains(':');

    private static string DetectProjectName(IReadOnlyList<string>? relativePaths)
    {
        if (relativePaths is null || relativePaths.Count == 0)
        {
            return $"Project {DateTime.UtcNow:yyyy-MM-dd}";
        }

        List<string> paths = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').TrimStart('/'))
            .ToList();
        List<string> topDirectories = paths
            .Select(path => path.Split('/').FirstOrDefault() ?? string.Empty)
            .Where(directory => directory.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (topDirectories.Count == 1 && paths.Any(path => path.Contains('/')))
        {
            return topDirectories[0];
        }

        string? dominantExtension = paths
            .Select(path => Path.GetExtension(path).ToLowerInvariant())
            .Where(extension => extension.Length > 0)
            .GroupBy(extension => extension)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        string language = dominantExtension switch
        {
            ".cs" => "C#",
            ".js" or ".jsx" => "JavaScript",
            ".ts" or ".tsx" => "TypeScript",
            ".py" => "Python",
            ".go" => "Go",
            ".rs" => "Rust",
            ".java" => "Java",
            ".c" => "C",
            ".cpp" or ".cc" or ".cxx" => "C++",
            null => "Code",
            _ => dominantExtension.TrimStart('.').ToUpperInvariant()
        };
        return $"{language} Project {DateTime.UtcNow:yyyy-MM-dd}";
    }

    private static string? ReadDisplayName(string folderPath)
    {
        string metadataPath = Path.Combine(folderPath, ".project-meta.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return document.RootElement.TryGetProperty("displayName", out JsonElement value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
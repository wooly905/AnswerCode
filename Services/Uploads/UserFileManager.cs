using System.Security.Claims;

namespace AnswerCode.Services.Uploads;

public sealed class UserFileManager(
    IUserStorageService userStorage,
    ISourceUploadService uploadService,
    ISourceFilePolicy filePolicy,
    ILogger<UserFileManager> logger) : IUserFileManager
{
    private const string MetadataFileName = ".project-meta.json";

    public IReadOnlyList<ManagedProjectNode> GetTree(ClaimsPrincipal user)
    {
        string root = userStorage.GetUserStoragePath(user);
        return uploadService.ListUserProjects(user)
            .Select(project => new ManagedProjectNode(
                project.FolderId,
                project.DisplayName,
                BuildChildren(new DirectoryInfo(Path.Combine(root, project.FolderId)), string.Empty)))
            .ToList();
    }

    public ManagedListResult ListEntries(ClaimsPrincipal user, string folderId, string? path)
    {
        string? directoryPath = ResolvePath(user, folderId, path);
        if (directoryPath is null)
        {
            return new(ManagedFileStatus.InvalidPath, []);
        }

        var directory = new DirectoryInfo(directoryPath);
        if (!directory.Exists)
        {
            return new(ManagedFileStatus.NotFound, []);
        }

        string prefix = NormalizeRelativePath(path);
        List<ManagedEntry> entries = directory.GetDirectories()
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(child => new ManagedEntry(child.Name, Combine(prefix, child.Name), true, 0, child.LastWriteTimeUtc))
            .Concat(directory.GetFiles()
                .Where(file => !IsMetadataFile(file.Name))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => new ManagedEntry(file.Name, Combine(prefix, file.Name), false, file.Length, file.LastWriteTimeUtc)))
            .ToList();
        return new(ManagedFileStatus.Success, entries);
    }

    public ManagedFileStatus DeleteFile(ClaimsPrincipal user, string folderId, string path)
    {
        string? filePath = string.IsNullOrWhiteSpace(path) ? null : ResolvePath(user, folderId, path);
        if (filePath is null || IsMetadataFile(Path.GetFileName(filePath)))
        {
            return ManagedFileStatus.InvalidPath;
        }

        if (!File.Exists(filePath))
        {
            return ManagedFileStatus.NotFound;
        }

        try
        {
            File.Delete(filePath);
            logger.LogInformation("Manage: deleted file {Path} in {FolderId}", path, folderId);
            return ManagedFileStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Manage: failed to delete file {Path} in {FolderId}", path, folderId);
            return ManagedFileStatus.Failed;
        }
    }

    public ManagedFileStatus DeleteFolder(ClaimsPrincipal user, string folderId, string? path)
    {
        string? directoryPath = ResolvePath(user, folderId, path);
        if (directoryPath is null)
        {
            return ManagedFileStatus.InvalidPath;
        }

        if (!Directory.Exists(directoryPath))
        {
            return ManagedFileStatus.NotFound;
        }

        try
        {
            Directory.Delete(directoryPath, recursive: true);
            logger.LogInformation("Manage: deleted folder '{Path}' in {FolderId}", path, folderId);
            return ManagedFileStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Manage: failed to delete folder '{Path}' in {FolderId}", path, folderId);
            return ManagedFileStatus.Failed;
        }
    }

    public ManagedFileStatus Rename(ClaimsPrincipal user, string folderId, string path, string newName)
    {
        string? sourcePath = string.IsNullOrWhiteSpace(path) ? null : ResolvePath(user, folderId, path);
        newName = newName?.Trim() ?? string.Empty;
        if (sourcePath is null || !IsValidName(newName) || IsMetadataFile(Path.GetFileName(sourcePath)))
        {
            return ManagedFileStatus.InvalidPath;
        }

        bool isDirectory = Directory.Exists(sourcePath);
        if (!isDirectory && !File.Exists(sourcePath))
        {
            return ManagedFileStatus.NotFound;
        }

        // Renamed files must still pass the upload policy, e.g. no "A.cs" -> "A.exe".
        if (!isDirectory && !filePolicy.IsAllowed(newName))
        {
            return ManagedFileStatus.InvalidPath;
        }

        string targetPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, newName);
        bool caseOnlyChange = string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase);
        if (!caseOnlyChange && (File.Exists(targetPath) || Directory.Exists(targetPath)))
        {
            return ManagedFileStatus.Conflict;
        }

        try
        {
            if (isDirectory)
            {
                Directory.Move(sourcePath, targetPath);
            }
            else
            {
                File.Move(sourcePath, targetPath);
            }

            logger.LogInformation("Manage: renamed '{Path}' to '{NewName}' in {FolderId}", path, newName, folderId);
            return ManagedFileStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Manage: failed to rename '{Path}' in {FolderId}", path, folderId);
            return ManagedFileStatus.Failed;
        }
    }

    public ManagedFileStatus CreateFolder(ClaimsPrincipal user, string folderId, string? path, string name)
    {
        string? parentPath = ResolvePath(user, folderId, path);
        name = name?.Trim() ?? string.Empty;
        if (parentPath is null || !IsValidName(name))
        {
            return ManagedFileStatus.InvalidPath;
        }

        if (!Directory.Exists(parentPath))
        {
            return ManagedFileStatus.NotFound;
        }

        string targetPath = Path.Combine(parentPath, name);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            return ManagedFileStatus.Conflict;
        }

        try
        {
            Directory.CreateDirectory(targetPath);
            logger.LogInformation("Manage: created folder '{Name}' under '{Path}' in {FolderId}", name, path, folderId);
            return ManagedFileStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Manage: failed to create folder '{Name}' in {FolderId}", name, folderId);
            return ManagedFileStatus.Failed;
        }
    }

    // An empty path resolves to the project root; null means the input is invalid.
    private string? ResolvePath(ClaimsPrincipal user, string folderId, string? path)
    {
        if (!SourceUploadService.IsValidFolderId(folderId))
        {
            return null;
        }

        string projectRoot = Path.Combine(userStorage.GetUserStoragePath(user), folderId);
        return string.IsNullOrWhiteSpace(path)
            ? projectRoot
            : filePolicy.ResolveDestinationPath(projectRoot, path);
    }

    private static IReadOnlyList<ManagedTreeNode> BuildChildren(DirectoryInfo directory, string relativePath) =>
        directory.Exists
            ? directory.GetDirectories()
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .Select(child =>
                {
                    string childPath = Combine(relativePath, child.Name);
                    return new ManagedTreeNode(child.Name, childPath, BuildChildren(child, childPath));
                })
                .ToList()
            : [];

    private static string NormalizeRelativePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').Trim('/');

    private static string Combine(string prefix, string name) =>
        prefix.Length == 0 ? name : $"{prefix}/{name}";

    private static bool IsMetadataFile(string name) =>
        name.Equals(MetadataFileName, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidName(string name) =>
        name.Length > 0
        && name is not "." and not ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains('/')
        && !name.Contains('\\')
        && !IsMetadataFile(name);
}

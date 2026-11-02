using System.Security.Claims;

namespace AnswerCode.Services.Uploads;

public interface IUserFileManager
{
    IReadOnlyList<ManagedProjectNode> GetTree(ClaimsPrincipal user);

    ManagedListResult ListEntries(ClaimsPrincipal user, string folderId, string? path);

    ManagedFileStatus DeleteFile(ClaimsPrincipal user, string folderId, string path);

    ManagedFileStatus DeleteFolder(ClaimsPrincipal user, string folderId, string? path);

    ManagedFileStatus Rename(ClaimsPrincipal user, string folderId, string path, string newName);

    ManagedFileStatus CreateFolder(ClaimsPrincipal user, string folderId, string? path, string name);
}

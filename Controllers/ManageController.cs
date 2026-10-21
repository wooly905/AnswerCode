using AnswerCode.Services.Uploads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnswerCode.Controllers;

[ApiController]
[Route("api/manage")]
[Authorize]
public sealed class ManageController(IUserFileManager fileManager) : ControllerBase
{
    [HttpGet("tree")]
    public IActionResult GetTree() => Ok(fileManager.GetTree(User));

    [HttpGet("entries")]
    public IActionResult ListEntries([FromQuery] string folderId, [FromQuery] string? path = null)
    {
        ManagedListResult result = fileManager.ListEntries(User, folderId, path);
        return result.Status == ManagedFileStatus.Success ? Ok(result.Entries) : ToError(result.Status);
    }

    [HttpDelete("file")]
    public IActionResult DeleteFile([FromQuery] string folderId, [FromQuery] string path)
    {
        ManagedFileStatus status = fileManager.DeleteFile(User, folderId, path);
        return status == ManagedFileStatus.Success ? Ok(new { message = "File deleted" }) : ToError(status);
    }

    [HttpDelete("folder")]
    public IActionResult DeleteFolder([FromQuery] string folderId, [FromQuery] string? path = null)
    {
        ManagedFileStatus status = fileManager.DeleteFolder(User, folderId, path);
        return status == ManagedFileStatus.Success ? Ok(new { message = "Folder deleted" }) : ToError(status);
    }

    [HttpPatch("rename")]
    public IActionResult Rename([FromBody] RenameRequest request)
    {
        ManagedFileStatus status = fileManager.Rename(User, request.FolderId, request.Path, request.NewName);
        return status == ManagedFileStatus.Success ? Ok(new { message = "Renamed" }) : ToError(status);
    }

    public sealed record RenameRequest(string FolderId, string Path, string NewName);

    private IActionResult ToError(ManagedFileStatus status) => status switch
    {
        ManagedFileStatus.InvalidPath => BadRequest(new { error = "Invalid path or name" }),
        ManagedFileStatus.NotFound => NotFound(new { error = "Not found" }),
        ManagedFileStatus.Conflict => Conflict(new { error = "A file or folder with that name already exists" }),
        _ => StatusCode(500, new { error = "Operation failed" })
    };
}

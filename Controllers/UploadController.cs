using AnswerCode.Services.Uploads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnswerCode.Controllers;

[ApiController]
[Route("api/CodeQA")]
public sealed class UploadController(ISourceUploadService uploadService) : ControllerBase
{
    [HttpPost("upload")]
    [RequestSizeLimit(SourceUploadService.AuthenticatedMaxUploadBytes + 1024 * 1024)]
    [RequestFormLimits(
        MultipartBodyLengthLimit = SourceUploadService.AuthenticatedMaxUploadBytes + 1024 * 1024,
        ValueCountLimit = 10000)]
    public async Task<IActionResult> UploadSourceCode(
        [FromForm] List<IFormFile> files,
        [FromForm] List<string>? relativePaths,
        [FromForm] string? folderId,
        [FromForm] string? projectName)
    {
        try
        {
            SourceUploadResult result = await uploadService.UploadAsync(
                files,
                relativePaths,
                folderId,
                projectName,
                User,
                HttpContext.RequestAborted);
            return Ok(result);
        }
        catch (SourceUploadException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("upload/{folderId}")]
    public IActionResult DeleteSourceCode(string folderId, [FromQuery] string? deleteToken = null)
    {
        SourceDeleteResult result = uploadService.Delete(folderId, deleteToken, User);
        return result.Status switch
        {
            SourceDeleteStatus.Success => Ok(new { message = result.Message, folderId = result.FolderId }),
            SourceDeleteStatus.InvalidFolderId => BadRequest(new { error = result.Message }),
            SourceDeleteStatus.Unauthorized => Unauthorized(new { error = result.Message }),
            SourceDeleteStatus.NotFound => NotFound(new { error = result.Message }),
            _ => StatusCode(500, new { error = result.Message })
        };
    }

    [HttpPost("upload/{folderId}/cleanup")]
    public IActionResult CleanupSourceCode(string folderId, [FromQuery] string? deleteToken = null) =>
        DeleteSourceCode(folderId, deleteToken);

    [HttpGet("uploads")]
    [Authorize]
    public IActionResult ListUploads() => Ok(uploadService.ListAnonymousUploads().Select(project => new
    {
        folderId = project.FolderId,
        createdUtc = project.CreatedAt,
        fileCount = project.FileCount
    }));

    [HttpGet("user-folders")]
    [Authorize]
    public IActionResult ListUserFolders() => Ok(uploadService.ListUserProjects(User));
}
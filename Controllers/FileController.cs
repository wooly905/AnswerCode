using AnswerCode.Services;
using AnswerCode.Services.Uploads;
using Microsoft.AspNetCore.Mvc;

namespace AnswerCode.Controllers;

[ApiController]
[Route("api/CodeQA")]
public sealed class FileController(
    ICodeExplorerService codeExplorer,
    IProjectPathResolver projectPathResolver) : ControllerBase
{
    [HttpGet("structure")]
    public async Task<ActionResult<string>> GetProjectStructure([FromQuery] string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return BadRequest(new { error = "ProjectPath is required" });
        }

        string resolvedPath = projectPathResolver.ResolveProjectPath(projectPath, User);
        if (string.IsNullOrEmpty(resolvedPath) || !Directory.Exists(resolvedPath))
        {
            return BadRequest(new { error = $"Project path does not exist: {projectPath}" });
        }

        return Ok(new { structure = await codeExplorer.GetProjectStructureAsync(resolvedPath, 4) });
    }

    [HttpGet("file")]
    public async Task<ActionResult<string>> ReadFile(
        [FromQuery] string filePath,
        [FromQuery] int? maxLines = null,
        [FromQuery] int? offset = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return BadRequest(new { error = "FilePath is required" });
        }

        if (!projectPathResolver.IsPathAllowed(filePath, User))
        {
            return Forbid();
        }

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = $"File not found: {filePath}" });
        }

        return Ok(new { content = await codeExplorer.ReadFileAsync(filePath, maxLines, offset) });
    }
}
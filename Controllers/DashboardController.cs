using System.Text.Json;
using AnswerCode.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnswerCode.Controllers;

/// <summary>
/// API endpoints for the authenticated dashboard.
/// All endpoints require Google login.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IUserStorageService _userStorage;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(IUserStorageService userStorage,
                               IWebHostEnvironment env,
                               ILogger<DashboardController> logger)
    {
        _userStorage = userStorage;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Get storage usage for the current user.
    /// </summary>
    [HttpGet("usage")]
    public IActionResult GetUsage()
    {
        return Ok(new
        {
            usedMB = Math.Round(_userStorage.GetUsageMB(User), 2),
            maxMB = _userStorage.GetMaxSizeMB()
        });
    }

    /// <summary>
    /// List all folders belonging to the current user.
    /// </summary>
    [HttpGet("folders")]
    public IActionResult ListFolders()
    {
        var userPath = _userStorage.GetUserStoragePath(User);

        if (!Directory.Exists(userPath))
            return Ok(Array.Empty<object>());

        var folders = Directory.GetDirectories(userPath)
            .Select(d => new DirectoryInfo(d))
            .Select(d =>
            {
                var allFiles = d.GetFiles("*", SearchOption.AllDirectories)
                    .Where(f => f.Name != ".project-meta.json").ToArray();
                return new
                {
                    folderId = d.Name,
                    displayName = CodeQAController.ReadDisplayName(d.FullName) ?? d.Name,
                    fileCount = allFiles.Length,
                    sizeMB = Math.Round(allFiles.Sum(f => f.Length) / (1024.0 * 1024.0), 2),
                    createdAt = d.CreationTimeUtc
                };
            })
            .OrderByDescending(f => f.createdAt)
            .ToList();

        return Ok(folders);
    }

    /// <summary>
    /// Rename a folder (update its displayName).
    /// </summary>
    [HttpPatch("folders/{folderId}")]
    public async Task<IActionResult> RenameFolder(string folderId, [FromBody] RenameFolderRequest request)
    {
        if (string.IsNullOrWhiteSpace(folderId) || folderId.Contains("..") || folderId.Contains('/') || folderId.Contains('\\'))
            return BadRequest(new { error = "Invalid folder ID" });

        if (string.IsNullOrWhiteSpace(request?.DisplayName))
            return BadRequest(new { error = "displayName is required" });

        var userPath = _userStorage.GetUserStoragePath(User);
        var folderPath = Path.Combine(userPath, folderId);

        if (!Directory.Exists(folderPath))
            return NotFound(new { error = $"Folder not found: {folderId}" });

        var metaPath = Path.Combine(folderPath, ".project-meta.json");
        object meta;
        if (System.IO.File.Exists(metaPath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    await System.IO.File.ReadAllTextAsync(metaPath)) ?? new();
                existing["displayName"] = request.DisplayName.Trim();
                meta = existing;
            }
            catch
            {
                meta = new { displayName = request.DisplayName.Trim(), createdAt = DateTime.UtcNow };
            }
        }
        else
        {
            meta = new { displayName = request.DisplayName.Trim(), createdAt = DateTime.UtcNow };
        }

        await System.IO.File.WriteAllTextAsync(metaPath,
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

        _logger.LogInformation("Dashboard: renamed folder {FolderId} to '{DisplayName}'", folderId, request.DisplayName.Trim());
        return Ok(new { folderId, displayName = request.DisplayName.Trim() });
    }

    public class RenameFolderRequest
    {
        public string? DisplayName { get; set; }
    }

    /// <summary>
    /// Delete a folder belonging to the current user.
    /// </summary>
    [HttpDelete("folders/{folderId}")]
    public IActionResult DeleteFolder(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId) || folderId.Contains("..") || folderId.Contains('/') || folderId.Contains('\\'))
            return BadRequest(new { error = "Invalid folder ID" });

        var userPath = _userStorage.GetUserStoragePath(User);
        var folderPath = Path.Combine(userPath, folderId);

        if (!Directory.Exists(folderPath))
            return NotFound(new { error = $"Folder not found: {folderId}" });

        try
        {
            Directory.Delete(folderPath, recursive: true);
            _logger.LogInformation("Dashboard: deleted user folder {FolderId}", folderId);
            return Ok(new
            {
                message = "Folder deleted",
                folderId,
                usedMB = Math.Round(_userStorage.GetUsageMB(User), 2),
                maxMB = _userStorage.GetMaxSizeMB()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard: failed to delete folder {FolderId}", folderId);
            return StatusCode(500, new { error = "Failed to delete folder" });
        }
    }
}

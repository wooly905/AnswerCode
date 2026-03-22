using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnswerCode.Models;
using AnswerCode.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnswerCode.Controllers;

/// <summary>
/// Code Q&A API Controller
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CodeQAController : ControllerBase
{
    private readonly IAgentService _agentService;
    private readonly ICodeExplorerService _codeExplorer;
    private readonly ILLMServiceFactory _llmFactory;
    private readonly ILogger<CodeQAController> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly IUserStorageService _userStorage;

    /// <summary>
    /// Max upload size: 20 MB (anonymous), 300 MB (authenticated - enforced via quota)
    /// </summary>
    private const long _maxUploadBytes = 20 * 1024 * 1024;
    private const long _maxAuthUploadBytes = 300 * 1024 * 1024;

    public CodeQAController(IAgentService agentService,
                            ICodeExplorerService codeExplorer,
                            ILLMServiceFactory llmFactory,
                            IWebHostEnvironment env,
                            ILogger<CodeQAController> logger,
                            IUserStorageService userStorage)
    {
        _agentService = agentService;
        _codeExplorer = codeExplorer;
        _llmFactory = llmFactory;
        _env = env;
        _logger = logger;
        _userStorage = userStorage;
    }

    private bool IsAuthenticated => User.Identity?.IsAuthenticated == true;

    /// <summary>
    /// Maps anonymous folderId → deleteToken (SHA-256 hex).
    /// Tokens are issued at upload time and required for delete/cleanup.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> _deleteTokens = new();

    private static string GenerateDeleteToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    /// <summary>
    /// Validate that a resolved path is within an allowed storage directory
    /// (anonymous upload storage or the authenticated user's own storage).
    /// Prevents path-traversal attacks.
    /// </summary>
    private bool IsPathWithinAllowedStorage(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        // Allow anonymous upload storage
        var anonStorage = Path.GetFullPath(Path.Combine(_env.WebRootPath, "source-code"));
        if (fullPath.StartsWith(anonStorage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(anonStorage, StringComparison.OrdinalIgnoreCase))
            return true;

        // Allow authenticated user's own storage
        if (IsAuthenticated)
        {
            var userStorage = Path.GetFullPath(_userStorage.GetUserStoragePath(User));
            if (fullPath.StartsWith(userStorage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.Equals(userStorage, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ────────────────────────────────────────────
    //  Source-code upload / delete
    // ────────────────────────────────────────────

    private static readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb",     // .NET
        ".js", ".ts", ".jsx", ".tsx", // Node.js
        ".py",                   // Python
        ".go",                   // Go
        ".rs",                   // Rust
        ".java",                 // Java
        ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx", // C/C++
        ".htm", ".html", ".css", ".md", ".json", ".xml", ".toml", ".yml", ".yaml", ".sql",
        ".csproj", ".fsproj", ".vbproj", ".sln"
    };

    private static readonly HashSet<string> _allowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json",
        "package-lock.json",
        "requirements.txt",
        "pyproject.toml",
        "go.mod",
        "go.sum",
        "Cargo.toml",
        "Cargo.lock",
        "pom.xml",
        "build.gradle",
        "CMakeLists.txt",
        "Dockerfile",
        ".gitignore",
        ".editorconfig",
        "Makefile"
    };

    private static readonly HashSet<string> _ignoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "node_modules",
        "bin",
        "obj",
        ".vs",
        ".vscode",
        ".idea",
        "dist",
        "build",
        "out",
        "target",
        "__pycache__",
        "venv",
        ".venv"
    };

    private static bool IsValidFile(string filePath)
    {
        var parts = filePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Any(_ignoredDirs.Contains))
        {
            return false;
        }

        string fileName = Path.GetFileName(filePath);

        if (_allowedFiles.Contains(fileName))
        {
            return true;
        }

        string ext = Path.GetExtension(fileName);

        return _allowedExtensions.Contains(ext);
    }

    private async Task<bool> IsSafeFileContentAsync(IFormFile file)
    {
        if (file.Length < 4)
        {
            return true; // Too small for standard executable headers
        }

        using var stream = file.OpenReadStream();
        byte[] buffer = new byte[4];
        int bytesRead = await stream.ReadAsync(buffer, 0, 4);

        if (bytesRead < 2)
        {
            return true;
        }

        // Check for Windows PE (EXE, DLL) - "MZ" (4D 5A)
        if (buffer[0] == 0x4D && buffer[1] == 0x5A)
        {
            return false;
        }

        if (bytesRead >= 4)
        {
            // Check for ELF (Linux executable) - "\x7fELF" (7F 45 4C 46)
            if (buffer[0] == 0x7F
                && buffer[1] == 0x45
                && buffer[2] == 0x4C
                && buffer[3] == 0x46)
            {
                return false;
            }

            // Check for Mach-O (macOS executable)
            if ((buffer[0] == 0xFE && buffer[1] == 0xED && buffer[2] == 0xFA && buffer[3] == 0xCE) || // FEEDFACE
                (buffer[0] == 0xCE && buffer[1] == 0xFA && buffer[2] == 0xED && buffer[3] == 0xFE) || // CEFAEDFE
                (buffer[0] == 0xFE && buffer[1] == 0xED && buffer[2] == 0xFA && buffer[3] == 0xCF) || // FEEDFACF
                (buffer[0] == 0xCF && buffer[1] == 0xFA && buffer[2] == 0xED && buffer[3] == 0xFE) || // CFFAEDFE
                (buffer[0] == 0xCA && buffer[1] == 0xFE && buffer[2] == 0xBA && buffer[3] == 0xBE))   // CAFEBABE (Fat binary)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Upload source code files.
    /// Files are stored under wwwroot/source-code/{randomId}/
    /// preserving relative paths sent via the "relativePaths" form values.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(_maxAuthUploadBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = _maxAuthUploadBytes + 1024 * 1024, ValueCountLimit = 10000)]
    public async Task<IActionResult> UploadSourceCode([FromForm] List<IFormFile> files,
                                                      [FromForm] List<string>? relativePaths,
                                                      [FromForm] string? folderId,
                                                      [FromForm] string? projectName)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequest(new { error = "No files provided" });
        }

        bool persistent = IsAuthenticated;
        long sizeLimit = persistent ? _maxAuthUploadBytes : _maxUploadBytes;

        // ── size gate ──
        long totalSize = files.Sum(f => f.Length);

        if (totalSize > sizeLimit)
        {
            var limitMB = sizeLimit / (1024.0 * 1024.0);
            return BadRequest(new
            {
                error = $"Total upload size ({totalSize / (1024.0 * 1024.0):F2} MB) exceeds the {limitMB:F0} MB limit."
            });
        }

        // ── quota check for authenticated users ──
        if (persistent && !_userStorage.CheckQuota(User, totalSize))
        {
            var maxMB = _userStorage.GetMaxSizeMB();
            var usedMB = _userStorage.GetUsageMB(User);
            return BadRequest(new
            {
                error = $"Storage quota exceeded. Used: {usedMB:F1} MB / {maxMB} MB. Please delete some projects first."
            });
        }

        if (string.IsNullOrWhiteSpace(folderId))
        {
            folderId = Guid.NewGuid().ToString("N")[..12]; // short random id
        }
        else
        {
            if (folderId.Contains("..") || folderId.Contains('/') || folderId.Contains('\\'))
            {
                return BadRequest(new { error = "Invalid folder ID" });
            }
        }

        // Authenticated users: store under users/{hashedEmail}/{folderId}
        // Anonymous users: store under source-code/{folderId}
        string destRoot;
        if (persistent)
        {
            var userPath = _userStorage.GetUserStoragePath(User);
            destRoot = Path.Combine(userPath, folderId);
        }
        else
        {
            destRoot = Path.Combine(_env.WebRootPath, "source-code", folderId);
        }
        Directory.CreateDirectory(destRoot);

        _logger.LogInformation("Uploading {Count} files ({Size} bytes) to {Dest}", files.Count, totalSize, destRoot);

        int saved = 0;
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            // Use the provided relative path if available, otherwise use the file name
            string relativePath = (relativePaths != null && i < relativePaths.Count && !string.IsNullOrWhiteSpace(relativePaths[i]))
                ? relativePaths[i]
                : file.FileName;

            // Sanitize: prevent path traversal
            relativePath = relativePath.Replace('\\', '/');

            if (relativePath.Contains(".."))
            {
                _logger.LogWarning("Skipping file with suspicious path: {Path}", relativePath);
                continue;
            }

            if (!IsValidFile(relativePath))
            {
                _logger.LogDebug("Skipping unallowed file (not supported): {Path}", relativePath);
                continue;
            }

            // Magic Number Check: deeply inspect file content to block disguised executables
            if (!await IsSafeFileContentAsync(file))
            {
                _logger.LogWarning("Security Block: File {Path} has an executable binary signature (Magic Number) but is disguised as a text file. Upload rejected.", relativePath);
                continue;
            }

            string destPath = Path.Combine(destRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string? dir = Path.GetDirectoryName(destPath);

            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using var stream = new FileStream(destPath, FileMode.Create);
            await file.CopyToAsync(stream);
            saved++;
        }

        _logger.LogInformation("Upload complete: {Saved}/{Total} files saved to folder {Id}", saved, files.Count, folderId);

        long totalFolderSizeBytes = 0;
        int totalFolderFileCount = 0;

        if (Directory.Exists(destRoot))
        {
            var allFiles = Directory.GetFiles(destRoot, "*", SearchOption.AllDirectories);
            totalFolderFileCount = allFiles.Length;
            totalFolderSizeBytes = allFiles.Sum(f => new FileInfo(f).Length);
        }

        // ── write project metadata ──
        var displayName = !string.IsNullOrWhiteSpace(projectName)
            ? projectName.Trim()
            : DetectProjectName(relativePaths);
        var meta = new { displayName, createdAt = DateTime.UtcNow };
        var metaPath = Path.Combine(destRoot, ".project-meta.json");
        await System.IO.File.WriteAllTextAsync(metaPath,
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

        // Issue a delete token for anonymous uploads
        string? deleteToken = null;
        if (!persistent)
        {
            deleteToken = GenerateDeleteToken();
            _deleteTokens[folderId] = deleteToken;
        }

        return Ok(new
        {
            folderId,
            displayName,
            fileCount = totalFolderFileCount,
            totalSizeBytes = totalFolderSizeBytes,
            totalSizeMB = Math.Round(totalFolderSizeBytes / (1024.0 * 1024.0), 2),
            persistent,
            deleteToken
        });
    }

    /// <summary>
    /// Delete a previously uploaded source-code folder.
    /// Authenticated users can delete their own folders.
    /// Anonymous users must provide the deleteToken issued at upload time.
    /// </summary>
    [HttpDelete("upload/{folderId}")]
    public IActionResult DeleteSourceCode(string folderId, [FromQuery] string? deleteToken = null)
    {
        if (string.IsNullOrWhiteSpace(folderId) || folderId.Contains("..") || folderId.Contains('/') || folderId.Contains('\\'))
        {
            return BadRequest(new { error = "Invalid folder ID" });
        }

        // Authenticated users can delete their own folders without a token
        if (IsAuthenticated)
        {
            var userPath = _userStorage.GetUserStoragePath(User);
            var userCandidate = Path.Combine(userPath, folderId);
            if (Directory.Exists(userCandidate))
                return DoDelete(userCandidate, folderId);

            return NotFound(new { error = $"Folder not found: {folderId}" });
        }

        // Anonymous: require a valid delete token
        if (string.IsNullOrWhiteSpace(deleteToken)
            || !_deleteTokens.TryGetValue(folderId, out var expected)
            || !string.Equals(deleteToken, expected, StringComparison.Ordinal))
        {
            return Unauthorized(new { error = "Invalid or missing delete token" });
        }

        string folderPath = Path.Combine(_env.WebRootPath, "source-code", folderId);
        if (!Directory.Exists(folderPath))
        {
            _deleteTokens.TryRemove(folderId, out _);
            return NotFound(new { error = $"Folder not found: {folderId}" });
        }

        var result = DoDelete(folderPath, folderId);
        _deleteTokens.TryRemove(folderId, out _);
        return result;
    }

    private IActionResult DoDelete(string folderPath, string folderId)
    {
        try
        {
            Directory.Delete(folderPath, recursive: true);
            _logger.LogInformation("Deleted source-code folder: {Id}", folderId);
            return Ok(new { message = "Source code removed", folderId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting source-code folder {Id}", folderId);
            return StatusCode(500, new { error = "Failed to delete source code folder" });
        }
    }

    /// <summary>
    /// Cleanup endpoint called via navigator.sendBeacon() when the browser tab is closed.
    /// sendBeacon can only send POST requests, so this is a POST alias for delete.
    /// The deleteToken is passed as a query parameter.
    /// </summary>
    [HttpPost("upload/{folderId}/cleanup")]
    public IActionResult CleanupSourceCode(string folderId, [FromQuery] string? deleteToken = null)
    {
        return DeleteSourceCode(folderId, deleteToken);
    }

    /// <summary>
    /// List existing source-code upload folders (authenticated users only).
    /// </summary>
    [HttpGet("uploads")]
    [Authorize]
    public IActionResult ListUploads()
    {
        string root = Path.Combine(_env.WebRootPath, "source-code");

        if (!Directory.Exists(root))
        {
            return Ok(Array.Empty<object>());
        }

        var folders = Directory.GetDirectories(root)
            .Select(d => new DirectoryInfo(d))
            .Select(d => new
            {
                folderId = d.Name,
                createdUtc = d.CreationTimeUtc,
                fileCount = Directory.GetFiles(d.FullName, "*", SearchOption.AllDirectories).Length
            })
            .OrderByDescending(x => x.createdUtc)
            .ToList();

        return Ok(folders);
    }

    // ────────────────────────────────────────────
    //  Q&A endpoints
    // ────────────────────────────────────────────

    /// <summary>
    /// Ask a question about the code (Agentic mode)
    /// </summary>
    /// <param name="request">Question request</param>
    /// <returns>AI answer</returns>
    [HttpPost("ask")]
    [ProducesResponseType<AnswerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AnswerResponse>> AskQuestion([FromBody] QuestionRequest request)
    {
        _logger.LogInformation("Received question: {Question} for project: {ProjectPath}", request.Question, request.ProjectPath);

        // Validate input
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest(new { error = "Question is required" });
        }

        string projectPath = ResolveProjectPath(request.ProjectPath);

        _logger.LogInformation("Resolved project path: {ProjectPath}", projectPath);

        if (!Directory.Exists(projectPath))
        {
            return BadRequest(new { error = $"Project path does not exist: {projectPath}" });
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var sessionId = request.SessionId ?? Guid.NewGuid().ToString();

            // Run the agent (agentic tool-calling loop)
            var agentResult = await _agentService.RunAsync(request.Question,
                                                           projectPath,
                                                           sessionId,
                                                           request.ModelProvider,
                                                           request.UserRole);

            stopwatch.Stop();

            var response = new AnswerResponse
            {
                Answer = agentResult.Answer,
                RelevantFiles = agentResult.RelevantFiles,
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                SessionId = sessionId,
                ToolCallCount = agentResult.TotalToolCalls,
                IterationCount = agentResult.IterationCount,
                ToolCalls = agentResult.ToolCalls,
                TotalInputTokens = agentResult.TotalInputTokens,
                TotalOutputTokens = agentResult.TotalOutputTokens
            };

            _logger.LogInformation("Question answered in {ElapsedMs}ms ({ToolCalls} tool calls, {Iterations} iterations)",
                                   stopwatch.ElapsedMilliseconds,
                                   agentResult.TotalToolCalls,
                                   agentResult.IterationCount);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing question");
            return StatusCode(500, new { error = "An error occurred while processing your question" });
        }
    }

    /// <summary>
    /// Ask a question with SSE streaming progress (shows tool calls in real-time)
    /// </summary>
    [HttpPost("ask/stream")]
    public async Task AskQuestionStream([FromBody] QuestionRequest request)
    {
        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        if (string.IsNullOrWhiteSpace(request.Question))
        {
            await WriteSSE(new AgentEvent { Type = AgentEventType.Error, Summary = "Question is required" });
            return;
        }

        string projectPath = ResolveProjectPath(request.ProjectPath);

        if (!Directory.Exists(projectPath))
        {
            await WriteSSE(new AgentEvent { Type = AgentEventType.Error, Summary = $"Project path does not exist: {projectPath}" });
            return;
        }

        DateTime startTime = DateTime.UtcNow;
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString();

        try
        {
            var agentResult = await _agentService.RunAsync(request.Question,
                                                           projectPath,
                                                           WriteSSE,
                                                           sessionId,
                                                           request.ModelProvider,
                                                           request.UserRole);

            // Send final answer event
            var answerResponse = new AnswerResponse
            {
                Answer = agentResult.Answer,
                RelevantFiles = agentResult.RelevantFiles,
                ProcessingTimeMs = (DateTime.Now - startTime).Milliseconds,
                SessionId = sessionId,
                ToolCallCount = agentResult.TotalToolCalls,
                IterationCount = agentResult.IterationCount,
                ToolCalls = agentResult.ToolCalls,
                TotalInputTokens = agentResult.TotalInputTokens,
                TotalOutputTokens = agentResult.TotalOutputTokens
            };

            await WriteSSE(new AgentEvent
            {
                Type = AgentEventType.Answer,
                Result = answerResponse,
                TotalToolCalls = agentResult.TotalToolCalls
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in streaming question");
            await WriteSSE(new AgentEvent { Type = AgentEventType.Error, Summary = ex.Message });
        }
    }

    private async Task WriteSSE(AgentEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        });

        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync();
    }

    /// <summary>
    /// Get project structure
    /// </summary>
    [HttpGet("structure")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    public async Task<ActionResult<string>> GetProjectStructure([FromQuery] string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return BadRequest(new { error = "ProjectPath is required" });
        }

        if (!Directory.Exists(projectPath))
        {
            return BadRequest(new { error = $"Project path does not exist: {projectPath}" });
        }

        if (!IsPathWithinAllowedStorage(projectPath))
        {
            return Forbid();
        }

        var structure = await _codeExplorer.GetProjectStructureAsync(projectPath, 4);
        return Ok(new { structure });
    }

    /// <summary>
    /// Read file
    /// </summary>
    [HttpGet("file")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    public async Task<ActionResult<string>> ReadFile([FromQuery] string filePath,
                                                     [FromQuery] int? maxLines = null,
                                                     [FromQuery] int? offset = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return BadRequest(new { error = "FilePath is required" });
        }

        if (!IsPathWithinAllowedStorage(filePath))
        {
            return Forbid();
        }

        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = $"File not found: {filePath}" });
        }

        var content = await _codeExplorer.ReadFileAsync(filePath, maxLines, offset);
        return Ok(new { content });
    }

    /// <summary>
    /// Get available LLM providers with their display names
    /// </summary>
    [HttpGet("providers")]
    [ProducesResponseType<Dictionary<string, string>>(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, string>> GetProviders()
    {
        var providers = _llmFactory.GetProviderDisplayNames();
        return Ok(providers);
    }

    // ────────────────────────────────────────────
    //  Authenticated user folders
    // ────────────────────────────────────────────

    /// <summary>
    /// List authenticated user's uploaded folders (for project selector on main page).
    /// </summary>
    [HttpGet("user-folders")]
    [Authorize]
    public IActionResult ListUserFolders()
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
                    displayName = ReadDisplayName(d.FullName) ?? d.Name,
                    fileCount = allFiles.Length,
                    sizeMB = Math.Round(allFiles.Sum(f => f.Length) / (1024.0 * 1024.0), 2),
                    createdAt = d.CreationTimeUtc
                };
            })
            .OrderByDescending(f => f.createdAt)
            .ToList();

        return Ok(folders);
    }

    // ── helpers ──

    /// <summary>
    /// Resolve a folderId to its full filesystem path, checking both
    /// authenticated user storage and anonymous storage.
    /// </summary>
    private string ResolveFolderPath(string folderId)
    {
        // Check authenticated user's storage first
        if (IsAuthenticated)
        {
            var userPath = _userStorage.GetUserStoragePath(User);
            var userCandidate = Path.Combine(userPath, folderId);
            if (Directory.Exists(userCandidate))
                return userCandidate;
        }

        // Fall back to anonymous storage
        return Path.Combine(_env.WebRootPath, "source-code", folderId);
    }

    /// <summary>
    /// Resolve project path: if it's a folderId, resolve to the correct storage path,
    /// otherwise treat it as-is.
    /// </summary>
    private string ResolveProjectPath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        // If the input looks like a simple folder ID (no slashes, no colons),
        // resolve it relative to source-code/
        if (!input.Contains('/') && !input.Contains('\\') && !input.Contains(':'))
        {
            // Check authenticated user storage first
            if (IsAuthenticated)
            {
                var userPath = _userStorage.GetUserStoragePath(User);
                var userCandidate = Path.Combine(userPath, input);
                if (Directory.Exists(userCandidate))
                    return userCandidate;
            }

            // Then check anonymous storage
            string candidate = Path.Combine(_env.WebRootPath, "source-code", input);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        // For any other path (absolute or relative), resolve and validate
        string resolved = Path.IsPathRooted(input)
            ? Path.GetFullPath(input)
            : Path.GetFullPath(Path.Combine(_env.WebRootPath, "source-code", input));

        // Only allow paths within permitted storage directories
        if (!IsPathWithinAllowedStorage(resolved))
        {
            _logger.LogWarning("Blocked path traversal attempt: {Input}", input);
            return string.Empty;
        }

        return resolved;
    }

    /// <summary>
    /// Auto-detect a human-readable project name from the uploaded file paths.
    /// </summary>
    private static string DetectProjectName(List<string>? relativePaths)
    {
        if (relativePaths == null || relativePaths.Count == 0)
            return $"Project {DateTime.UtcNow:yyyy-MM-dd}";

        // Normalise to forward slashes and split into segments
        var paths = relativePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace('\\', '/').TrimStart('/'))
            .ToList();

        // If every path starts with the same top-level directory, use it as the project name
        var topDirs = paths
            .Select(p => p.Split('/').FirstOrDefault() ?? "")
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (topDirs.Count == 1 && paths.Any(p => p.Contains('/')))
            return topDirs[0];

        // Otherwise, derive from the dominant file extension
        var extGroups = paths
            .Select(p => Path.GetExtension(p).ToLowerInvariant())
            .Where(e => e.Length > 0)
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (extGroups.Count > 0)
        {
            var dominant = extGroups[0].Key;
            var lang = dominant switch
            {
                ".cs" => "C#",
                ".js" => "JavaScript",
                ".ts" => "TypeScript",
                ".tsx" => "TypeScript",
                ".jsx" => "JavaScript",
                ".py" => "Python",
                ".go" => "Go",
                ".rs" => "Rust",
                ".java" => "Java",
                ".c" => "C",
                ".cpp" or ".cc" or ".cxx" => "C++",
                _ => dominant.TrimStart('.')?.ToUpperInvariant() ?? "Code"
            };
            return $"{lang} Project {DateTime.UtcNow:yyyy-MM-dd}";
        }

        return $"Project {DateTime.UtcNow:yyyy-MM-dd}";
    }

    /// <summary>
    /// Read the displayName from a folder's .project-meta.json, if it exists.
    /// </summary>
    internal static string? ReadDisplayName(string folderPath)
    {
        var metaPath = Path.Combine(folderPath, ".project-meta.json");
        if (!System.IO.File.Exists(metaPath)) return null;
        try
        {
            var json = System.IO.File.ReadAllText(metaPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("displayName", out var val))
                return val.GetString();
        }
        catch { /* corrupted meta file – ignore */ }
        return null;
    }
}

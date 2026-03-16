using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnswerCode.Models;
using AnswerCode.Services;
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

    /// <summary>
    /// Max upload size: 20 MB
    /// </summary>
    private const long MaxUploadBytes = 20 * 1024 * 1024;

    public CodeQAController(IAgentService agentService,
                            ICodeExplorerService codeExplorer,
                            ILLMServiceFactory llmFactory,
                            IWebHostEnvironment env,
                            ILogger<CodeQAController> logger)
    {
        _agentService = agentService;
        _codeExplorer = codeExplorer;
        _llmFactory = llmFactory;
        _env = env;
        _logger = logger;
    }

    // ────────────────────────────────────────────
    //  Source-code upload / delete
    // ────────────────────────────────────────────

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb",     // .NET
        ".js", ".ts", ".jsx", ".tsx", // Node.js
        ".py",                   // Python
        ".go",                   // Go
        ".rs",                   // Rust
        ".java",                 // Java
        ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx", // C/C++
        ".html", ".css", ".md", ".json", ".xml", ".toml", ".yml", ".yaml", ".sql", ".sh", ".bat",
        ".csproj", ".fsproj", ".vbproj", ".sln"
    };

    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "package-lock.json",
        "requirements.txt", "pyproject.toml",
        "go.mod", "go.sum",
        "Cargo.toml", "Cargo.lock",
        "pom.xml", "build.gradle",
        "CMakeLists.txt",
        "Dockerfile", ".gitignore", ".editorconfig",
        "Makefile"
    };

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".vscode", ".idea",
        "dist", "build", "out", "target", "__pycache__", "venv", ".venv"
    };

    private bool IsValidFile(string filePath)
    {
        var parts = filePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => IgnoredDirs.Contains(p))) return false;

        string fileName = Path.GetFileName(filePath);
        if (AllowedFiles.Contains(fileName)) return true;

        string ext = Path.GetExtension(fileName);
        if (AllowedExtensions.Contains(ext)) return true;

        return false;
    }

    private async Task<bool> IsSafeFileContentAsync(IFormFile file)
    {
        if (file.Length < 4) return true; // Too small for standard executable headers

        using var stream = file.OpenReadStream();
        byte[] buffer = new byte[4];
        int bytesRead = await stream.ReadAsync(buffer, 0, 4);

        if (bytesRead < 2) return true;

        // Check for Windows PE (EXE, DLL) - "MZ" (4D 5A)
        if (buffer[0] == 0x4D && buffer[1] == 0x5A)
            return false;

        if (bytesRead >= 4)
        {
            // Check for ELF (Linux executable) - "\x7fELF" (7F 45 4C 46)
            if (buffer[0] == 0x7F && buffer[1] == 0x45 && buffer[2] == 0x4C && buffer[3] == 0x46)
                return false;

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
    [RequestSizeLimit(MaxUploadBytes + 1024 * 1024)] // a bit of headroom for form overhead
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes + 1024 * 1024,
                       ValueCountLimit = 10000)]
    public async Task<IActionResult> UploadSourceCode(
        [FromForm] List<IFormFile> files,
        [FromForm] List<string>? relativePaths,
        [FromForm] string? folderId)
    {
        if (files == null || files.Count == 0)
        {
            return BadRequest(new { error = "No files provided" });
        }

        // ── size gate ──
        long totalSize = files.Sum(f => f.Length);
        if (totalSize > MaxUploadBytes)
        {
            return BadRequest(new
            {
                error = $"Total upload size ({totalSize / (1024.0 * 1024.0):F2} MB) exceeds the 20 MB limit."
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

        string destRoot = Path.Combine(_env.WebRootPath, "source-code", folderId);
        Directory.CreateDirectory(destRoot);

        _logger.LogInformation("Uploading {Count} files ({Size} bytes) to {Dest}",
            files.Count, totalSize, destRoot);

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

        _logger.LogInformation("Upload complete: {Saved}/{Total} files saved to folder {Id}",
            saved, files.Count, folderId);

        long totalFolderSizeBytes = 0;
        int totalFolderFileCount = 0;
        if (Directory.Exists(destRoot))
        {
            var allFiles = Directory.GetFiles(destRoot, "*", SearchOption.AllDirectories);
            totalFolderFileCount = allFiles.Length;
            totalFolderSizeBytes = allFiles.Sum(f => new FileInfo(f).Length);
        }

        return Ok(new
        {
            folderId,
            fileCount = totalFolderFileCount,
            totalSizeBytes = totalFolderSizeBytes,
            totalSizeMB = Math.Round(totalFolderSizeBytes / (1024.0 * 1024.0), 2)
        });
    }

    /// <summary>
    /// Delete a previously uploaded source-code folder.
    /// </summary>
    [HttpDelete("upload/{folderId}")]
    public IActionResult DeleteSourceCode(string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId) || folderId.Contains("..") || folderId.Contains('/') || folderId.Contains('\\'))
        {
            return BadRequest(new { error = "Invalid folder ID" });
        }

        string folderPath = Path.Combine(_env.WebRootPath, "source-code", folderId);

        if (!Directory.Exists(folderPath))
        {
            return NotFound(new { error = $"Folder not found: {folderId}" });
        }

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
    /// List existing source-code upload folders.
    /// </summary>
    [HttpGet("uploads")]
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
        _logger.LogInformation("Received question: {Question} for project: {ProjectPath}",
                               request.Question,
                               request.ProjectPath);

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
            var agentResult = await _agentService.RunAsync(
                request.Question,
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

            _logger.LogInformation(
                "Question answered in {ElapsedMs}ms ({ToolCalls} tool calls, {Iterations} iterations)",
                stopwatch.ElapsedMilliseconds,
                agentResult.TotalToolCalls,
                agentResult.IterationCount);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing question");
            return StatusCode(500, new { error = "An error occurred while processing your question", details = ex.Message });
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
            var agentResult = await _agentService.RunAsync(
                request.Question,
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

    // ── helpers ──

    /// <summary>
    /// Resolve project path: if it's a folderId, resolve to source-code/{folderId},
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
            string candidate = Path.Combine(_env.WebRootPath, "source-code", input);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        // Resolve relative paths from the app base directory
        if (!Path.IsPathRooted(input))
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", input));
        }

        return input;
    }
}

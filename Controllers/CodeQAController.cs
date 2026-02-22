using System.Diagnostics;
using AnswerCode.Models;
using AnswerCode.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private readonly ProjectSettings _projectSettings;
    private readonly ILogger<CodeQAController> _logger;

    public CodeQAController(IAgentService agentService,
                            ICodeExplorerService codeExplorer,
                            ILLMServiceFactory llmFactory,
                            IOptions<ProjectSettings> projectSettings,
                            ILogger<CodeQAController> logger)
    {
        _agentService = agentService;
        _codeExplorer = codeExplorer;
        _llmFactory = llmFactory;
        _projectSettings = projectSettings.Value;
        _logger = logger;
    }

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

        // Use configured default path if not provided
        string projectPath = string.IsNullOrWhiteSpace(request.ProjectPath)
            ? _projectSettings.DefaultPath
            : request.ProjectPath;

        if (!Path.IsPathRooted(projectPath))
        {
            projectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", projectPath));
        }

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

        // Use configured default path if not provided
        string projectPath = string.IsNullOrWhiteSpace(request.ProjectPath)
            ? _projectSettings.DefaultPath
            : request.ProjectPath;
        if (!Path.IsPathRooted(projectPath))
        {
            projectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", projectPath));
        }

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

    /// <summary>
    /// Get the default project path from configuration
    /// </summary>
    [HttpGet("settings/defaultPath")]
    [ProducesResponseType<object>(StatusCodes.Status200OK)]
    public ActionResult GetDefaultProjectPath()
    {
        return Ok(new { defaultPath = _projectSettings.DefaultPath });
    }
}

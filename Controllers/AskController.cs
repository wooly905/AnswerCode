using System.Text.Json;
using System.Text.Json.Serialization;
using AnswerCode.Models;
using AnswerCode.Services;
using AnswerCode.Services.Questions;
using AnswerCode.Services.Uploads;
using Microsoft.AspNetCore.Mvc;

namespace AnswerCode.Controllers;

[ApiController]
[Route("api/CodeQA")]
public sealed class AskController(
    IQuestionExecutionService questionExecutionService,
    IProjectPathResolver projectPathResolver,
    IUserInputService userInputService,
    ILLMServiceFactory llmFactory,
    ILogger<AskController> logger) : ControllerBase
{
    [HttpPost("ask")]
    [ProducesResponseType<AnswerResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AnswerResponse>> AskQuestion([FromBody] QuestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest(new { error = "Question is required" });
        }

        string projectPath = projectPathResolver.ResolveProjectPath(request.ProjectPath, User);
        if (!Directory.Exists(projectPath))
        {
            return BadRequest(new { error = $"Project path does not exist: {projectPath}" });
        }

        try
        {
            return Ok(await questionExecutionService.ExecuteAsync(
                request,
                projectPath,
                cancellationToken: HttpContext.RequestAborted));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing question");
            return StatusCode(500, new { error = "An error occurred while processing your question" });
        }
    }

    [HttpPost("ask/stream")]
    public async Task AskQuestionStream([FromBody] QuestionRequest request)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        if (string.IsNullOrWhiteSpace(request.Question))
        {
            await WriteSseAsync(new AgentEvent { Type = AgentEventType.Error, Summary = "Question is required" });
            return;
        }

        string projectPath = projectPathResolver.ResolveProjectPath(request.ProjectPath, User);
        if (!Directory.Exists(projectPath))
        {
            await WriteSseAsync(new AgentEvent { Type = AgentEventType.Error, Summary = $"Project path does not exist: {projectPath}" });
            return;
        }

        try
        {
            AnswerResponse answer = await questionExecutionService.ExecuteAsync(
                request,
                projectPath,
                WriteSseAsync,
                HttpContext.RequestAborted);
            await WriteSseAsync(new AgentEvent
            {
                Type = AgentEventType.Answer,
                Result = answer,
                TotalToolCalls = answer.ToolCallCount
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in streaming question");
            await WriteSseAsync(new AgentEvent { Type = AgentEventType.Error, Summary = ex.Message });
        }
    }

    [HttpPost("ask/answer")]
    public IActionResult SubmitUserAnswer([FromBody] UserAnswerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.QuestionId))
        {
            return BadRequest(new { error = "questionId is required" });
        }

        return userInputService.SubmitAnswer(request.QuestionId, request.Answer ?? string.Empty)
            ? Ok()
            : NotFound(new { error = "No pending question with that id — it may have already timed out or been answered." });
    }

    [HttpGet("providers")]
    public ActionResult<Dictionary<string, string>> GetProviders() => Ok(llmFactory.GetProviderDisplayNames());

    private async Task WriteSseAsync(AgentEvent evt)
    {
        string json = JsonSerializer.Serialize(evt, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        });
        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync();
    }
}
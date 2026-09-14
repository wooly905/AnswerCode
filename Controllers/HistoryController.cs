using AnswerCode.Services;
using Microsoft.AspNetCore.Mvc;

namespace AnswerCode.Controllers;

[ApiController]
[Route("api/CodeQA")]
public sealed class HistoryController(IConversationHistoryService conversationHistory) : ControllerBase
{
    [HttpGet("history/{sessionId}")]
    public ActionResult<List<ConversationTurn>> GetHistory(string sessionId) =>
        Ok(conversationHistory.GetHistory(sessionId));
}
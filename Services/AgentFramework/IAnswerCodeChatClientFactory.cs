using Microsoft.Extensions.AI;

namespace AnswerCode.Services.AgentFramework;

public interface IAnswerCodeChatClientFactory
{
    IChatClient Create(string? providerName = null);
}
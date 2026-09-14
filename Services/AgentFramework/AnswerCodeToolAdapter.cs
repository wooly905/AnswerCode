using AnswerCode.Services.Tools;
using Microsoft.Extensions.AI;

namespace AnswerCode.Services.AgentFramework;

/// <summary>
/// Adapts the registered AnswerCode tools for a single agent run.
/// </summary>
public sealed class AnswerCodeToolAdapter
{
    public IReadOnlyList<AITool> Adapt(IEnumerable<ITool> tools, ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(context);

        return [.. tools.Select(tool => (AITool)new AnswerCodeToolFunction(tool, context))];
    }
}
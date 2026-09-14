using System.Text.Json;
using AnswerCode.Services.Tools;
using Microsoft.Extensions.AI;

namespace AnswerCode.Services.AgentFramework;

/// <summary>
/// Exposes an existing AnswerCode tool as an Agent Framework function.
/// </summary>
public sealed class AnswerCodeToolFunction : AIFunction
{
    private static readonly JsonSerializerOptions _serializerOptions = JsonSerializerOptions.Web;
    private readonly ITool _tool;
    private readonly ToolContext _context;
    private readonly JsonElement _jsonSchema;

    public AnswerCodeToolFunction(ITool tool, ToolContext context)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _context = context ?? throw new ArgumentNullException(nameof(context));

        string schemaJson = tool.GetChatToolDefinition().FunctionParameters.ToString();
        using JsonDocument schema = JsonDocument.Parse(schemaJson);
        _jsonSchema = schema.RootElement.Clone();
    }

    public override string Name => _tool.Name;

    public override string Description => _tool.Description;

    public override JsonElement JsonSchema => _jsonSchema;

    public override JsonSerializerOptions JsonSerializerOptions => _serializerOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string argumentsJson = JsonSerializer.Serialize(arguments, _serializerOptions);
        return await _tool.ExecuteAsync(argumentsJson, _context)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
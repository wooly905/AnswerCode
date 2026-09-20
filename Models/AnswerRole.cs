using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnswerCode.Models;

[JsonConverter(typeof(AnswerRoleJsonConverter))]
public enum AnswerRole
{
    Developer,
    PM,
    CustomerService
}

public sealed class AnswerRoleJsonConverter : JsonConverter<AnswerRole>
{
    public override AnswerRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(c => char.IsDigit(c) || c == ',')
            || !Enum.TryParse(value, ignoreCase: true, out AnswerRole role)
            || !Enum.IsDefined(role))
        {
            throw new JsonException($"Unsupported answer role '{value}'.");
        }

        return role;
    }

    public override void Write(Utf8JsonWriter writer, AnswerRole value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value))
        {
            throw new JsonException($"Unsupported answer role '{value}'.");
        }

        writer.WriteStringValue(value.ToString());
    }
}
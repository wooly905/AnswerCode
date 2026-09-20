using System.Text.Json;
using AnswerCode.Models;
using Xunit;

namespace AnswerCode.Tests.Models;

public class QuestionRequestTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Developer", AnswerRole.Developer)]
    [InlineData("PM", AnswerRole.PM)]
    [InlineData("CustomerService", AnswerRole.CustomerService)]
    [InlineData("developer", AnswerRole.Developer)]
    public void Deserialize_WithKnownRole_ParsesRole(string value, AnswerRole expected)
    {
        QuestionRequest? request = JsonSerializer.Deserialize<QuestionRequest>($$"""{"userRole":"{{value}}"}""", _jsonOptions);

        Assert.Equal(expected, request?.UserRole);
    }

    [Fact]
    public void Deserialize_WithoutRole_LeavesRoleUnset()
    {
        QuestionRequest? request = JsonSerializer.Deserialize<QuestionRequest>("{}", _jsonOptions);

        Assert.Null(request?.UserRole);
    }

    [Theory]
    [InlineData("\"Unknown\"")]
    [InlineData("999")]
    [InlineData("\"999\"")]
    [InlineData("\"1\"")]
    [InlineData("\"Developer,PM\"")]
    public void Deserialize_WithInvalidRole_ThrowsJsonException(string roleJson)
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<QuestionRequest>($$"""{"userRole":{{roleJson}}}""", _jsonOptions));
    }
}
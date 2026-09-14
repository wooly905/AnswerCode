using AnswerCode.Services.Uploads;
using Microsoft.AspNetCore.DataProtection;

namespace AnswerCode.Tests.Services.Uploads;

public class DeleteTokenServiceTests : IDisposable
{
    private readonly string _keyDirectory = Directory.CreateTempSubdirectory("AnswerCodeKeys_").FullName;

    [Fact]
    public void Validate_IssuedTokenForFolder_ReturnsTrue()
    {
        var service = CreateService();

        string token = service.Issue("folder-a");

        Assert.True(service.Validate("folder-a", token));
        Assert.False(service.Validate("folder-b", token));
    }

    [Fact]
    public void Validate_InvalidToken_ReturnsFalse()
    {
        var service = CreateService();

        Assert.False(service.Validate("folder-a", "not-a-token"));
    }

    public void Dispose() => Directory.Delete(_keyDirectory, recursive: true);

    private DeleteTokenService CreateService() =>
        new(DataProtectionProvider.Create(new DirectoryInfo(_keyDirectory)));
}
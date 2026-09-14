using System.Security.Claims;
using System.Text;
using AnswerCode.Services;
using AnswerCode.Services.Uploads;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AnswerCode.Tests.Services.Uploads;

public class SourceUploadServiceTests : IDisposable
{
    private readonly string _webRoot = Directory.CreateTempSubdirectory("AnswerCodeUpload_").FullName;

    [Fact]
    public async Task UploadAsync_RootedRelativePath_DoesNotWriteOutsideDestination()
    {
        string outsidePath = Path.Combine(Path.GetTempPath(), $"AnswerCodeOutside_{Guid.NewGuid():N}.cs");
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(item => item.WebRootPath).Returns(_webRoot);
        var tokenService = new Mock<IDeleteTokenService>();
        tokenService.Setup(item => item.Issue("folder-a")).Returns("token");
        var service = new SourceUploadService(
            environment.Object,
            Mock.Of<IUserStorageService>(),
            new SourceFilePolicy(),
            tokenService.Object,
            NullLogger<SourceUploadService>.Instance);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("class Example {}"));
        IFormFile file = new FormFile(content, 0, content.Length, "files", "Example.cs");

        SourceUploadResult result = await service.UploadAsync(
            [file],
            [outsidePath],
            "folder-a",
            "Test",
            new ClaimsPrincipal());

        Assert.Equal(0, result.FileCount);
        Assert.False(File.Exists(outsidePath));
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);
}
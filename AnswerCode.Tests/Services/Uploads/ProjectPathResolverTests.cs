using System.Security.Claims;
using AnswerCode.Services;
using AnswerCode.Services.Uploads;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AnswerCode.Tests.Services.Uploads;

public class ProjectPathResolverTests : IDisposable
{
    private readonly string _webRoot = Directory.CreateTempSubdirectory("AnswerCodePaths_").FullName;

    [Fact]
    public void ResolveProjectPath_AnonymousFolderId_ResolvesInsideSourceCodeRoot()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_webRoot, "source-code", "folder-a")).FullName;
        var resolver = CreateResolver();

        string result = resolver.ResolveProjectPath("folder-a", new ClaimsPrincipal());

        Assert.Equal(folder, result);
    }

    [Fact]
    public void ResolveProjectPath_PathOutsideStorage_ReturnsEmpty()
    {
        var resolver = CreateResolver();

        string result = resolver.ResolveProjectPath(Path.GetTempPath(), new ClaimsPrincipal());

        Assert.Empty(result);
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    private ProjectPathResolver CreateResolver()
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(item => item.WebRootPath).Returns(_webRoot);
        return new ProjectPathResolver(
            environment.Object,
            Mock.Of<IUserStorageService>(),
            NullLogger<ProjectPathResolver>.Instance);
    }
}
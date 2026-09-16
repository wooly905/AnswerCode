using System.Security.Claims;
using AnswerCode.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Moq;

namespace AnswerCode.Tests.Services;

public class UserStorageServiceTests : IDisposable
{
    private readonly string _webRoot;

    public UserStorageServiceTests()
    {
        _webRoot = Directory.CreateTempSubdirectory("AnswerCodeTests_WebRoot_").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_webRoot, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    private UserStorageService CreateService(int? maxSizeMB = null)
    {
        var envMock = new Mock<IWebHostEnvironment>();
        envMock.Setup(e => e.WebRootPath).Returns(_webRoot);

        var configData = maxSizeMB is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["UserStorage:MaxSizeMB"] = maxSizeMB.Value.ToString() };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        return new UserStorageService(envMock.Object, configuration);
    }

    private static ClaimsPrincipal CreateUser(string? email)
    {
        var claims = email is null ? [] : new[] { new Claim(ClaimTypes.Email, email) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void GetUserStoragePath_NoEmailClaim_Throws()
    {
        var service = CreateService();
        var user = CreateUser(email: null);

        Assert.Throws<InvalidOperationException>(() => service.GetUserStoragePath(user));
    }

    [Fact]
    public void GetUserStoragePath_SameEmail_ReturnsSamePath()
    {
        var service = CreateService();
        var user = CreateUser("someone@example.com");

        var first = service.GetUserStoragePath(user);
        var second = service.GetUserStoragePath(user);

        Assert.Equal(first, second);
    }

    [Fact]
    public void GetUserStoragePath_IsCaseInsensitiveOnEmail()
    {
        var service = CreateService();

        var lower = service.GetUserStoragePath(CreateUser("someone@example.com"));
        var upper = service.GetUserStoragePath(CreateUser("SOMEONE@EXAMPLE.COM"));

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void GetUserStoragePath_DifferentEmails_ProduceDifferentPaths()
    {
        var service = CreateService();

        var a = service.GetUserStoragePath(CreateUser("a@example.com"));
        var b = service.GetUserStoragePath(CreateUser("b@example.com"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GetUserStoragePath_UsesLowercaseEmailDirectoryUnderWebRoot()
    {
        var service = CreateService();
        var user = CreateUser("Someone@Gmail.com");

        var path = service.GetUserStoragePath(user);

        Assert.True(Directory.Exists(path));
        Assert.Equal(Path.Combine(_webRoot, "source-code", "users", "someone@gmail.com"), path);
    }

    [Fact]
    public void GetUsageMB_NoFiles_ReturnsZero()
    {
        var service = CreateService();
        var user = CreateUser("someone@example.com");

        Assert.Equal(0, service.GetUsageMB(user));
    }

    [Fact]
    public void GetUsageMB_SumsFileSizesInBytesConvertedToMB()
    {
        var service = CreateService();
        var user = CreateUser("someone@example.com");
        var storagePath = service.GetUserStoragePath(user);
        File.WriteAllBytes(Path.Combine(storagePath, "file.bin"), new byte[1024 * 1024]); // exactly 1 MB

        Assert.Equal(1.0, service.GetUsageMB(user), precision: 3);
    }

    [Fact]
    public void CheckQuota_WithinLimit_ReturnsTrue()
    {
        var service = CreateService(maxSizeMB: 10);
        var user = CreateUser("someone@example.com");

        Assert.True(service.CheckQuota(user, additionalBytes: 5 * 1024 * 1024));
    }

    [Fact]
    public void CheckQuota_ExceedsLimit_ReturnsFalse()
    {
        var service = CreateService(maxSizeMB: 10);
        var user = CreateUser("someone@example.com");

        Assert.False(service.CheckQuota(user, additionalBytes: 11L * 1024 * 1024));
    }

    [Fact]
    public void GetMaxSizeMB_DefaultsTo300_WhenNotConfigured()
    {
        var service = CreateService();

        Assert.Equal(300, service.GetMaxSizeMB());
    }

    [Fact]
    public void GetMaxSizeMB_ReturnsConfiguredValue()
    {
        var service = CreateService(maxSizeMB: 42);

        Assert.Equal(42, service.GetMaxSizeMB());
    }
}

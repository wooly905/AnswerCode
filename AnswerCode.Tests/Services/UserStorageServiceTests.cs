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
    public void GetUserHashedId_NoEmailClaim_Throws()
    {
        var service = CreateService();
        var user = CreateUser(email: null);

        Assert.Throws<InvalidOperationException>(() => service.GetUserHashedId(user));
    }

    [Fact]
    public void GetUserHashedId_SameEmail_ReturnsSameHash()
    {
        var service = CreateService();
        var user = CreateUser("someone@example.com");

        var first = service.GetUserHashedId(user);
        var second = service.GetUserHashedId(user);

        Assert.Equal(first, second);
        Assert.Equal(16, first.Length);
    }

    [Fact]
    public void GetUserHashedId_IsCaseInsensitiveOnEmail()
    {
        var service = CreateService();

        var lower = service.GetUserHashedId(CreateUser("someone@example.com"));
        var upper = service.GetUserHashedId(CreateUser("SOMEONE@EXAMPLE.COM"));

        Assert.Equal(lower, upper);
    }

    [Fact]
    public void GetUserHashedId_DifferentEmails_ProduceDifferentHashes()
    {
        var service = CreateService();

        var a = service.GetUserHashedId(CreateUser("a@example.com"));
        var b = service.GetUserHashedId(CreateUser("b@example.com"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GetUserStoragePath_CreatesDirectoryUnderWebRoot()
    {
        var service = CreateService();
        var user = CreateUser("someone@example.com");

        var path = service.GetUserStoragePath(user);

        Assert.True(Directory.Exists(path));
        Assert.StartsWith(Path.Combine(_webRoot, "source-code", "users"), path);
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

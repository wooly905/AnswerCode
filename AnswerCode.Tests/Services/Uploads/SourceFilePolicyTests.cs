using AnswerCode.Services.Uploads;

namespace AnswerCode.Tests.Services.Uploads;

public class SourceFilePolicyTests
{
    private readonly SourceFilePolicy _policy = new();

    [Theory]
    [InlineData("C:/temp/evil.cs")]
    [InlineData("/temp/evil.cs")]
    [InlineData("\\temp\\evil.cs")]
    [InlineData("../evil.cs")]
    [InlineData("src/../../evil.cs")]
    public void ResolveDestinationPath_UnsafePath_ReturnsNull(string relativePath)
    {
        string root = Path.Combine(Path.GetTempPath(), "answer-code-upload");

        string? result = _policy.ResolveDestinationPath(root, relativePath);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveDestinationPath_NormalRelativePath_StaysInsideRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "answer-code-upload");

        string? result = _policy.ResolveDestinationPath(root, "src/Foo.cs");

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "Foo.cs")), result);
    }

    [Theory]
    [InlineData("src/Foo.cs", true)]
    [InlineData("package.json", true)]
    [InlineData("bin/Foo.cs", false)]
    [InlineData("payload.exe", false)]
    public void IsAllowed_AppliesSourceFilePolicy(string path, bool expected)
    {
        Assert.Equal(expected, _policy.IsAllowed(path));
    }
}
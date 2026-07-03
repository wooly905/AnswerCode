using AnswerCode.Services.Analysis;

namespace AnswerCode.Tests.Services.Analysis;

public class WorkspaceFileServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly WorkspaceFileService _service = new();

    public WorkspaceFileServiceTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("AnswerCodeSample_").FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    [Theory]
    [InlineData("Foo.cs", "csharp")]
    [InlineData("Foo.tsx", "typescript")]
    [InlineData("foo.js", "javascript")]
    [InlineData("foo.py", "python")]
    [InlineData("Foo.java", "java")]
    [InlineData("main.go", "go")]
    [InlineData("main.rs", "rust")]
    [InlineData("main.cpp", "cpp")]
    [InlineData("main.c", "c")]
    [InlineData("readme.md", null)]
    public void GetLanguageId_ReturnsExpectedLanguage(string fileName, string? expected)
    {
        Assert.Equal(expected, _service.GetLanguageId(fileName));
    }

    [Theory]
    [InlineData("FooTests.cs", true)]
    [InlineData("Foo.Spec.ts", true)]
    [InlineData("foo_test.py", true)]
    [InlineData("Foo.cs", false)]
    public void IsTestFile_DetectsByFileName(string fileName, bool expected)
    {
        Assert.Equal(expected, _service.IsTestFile(Path.Combine(_tempRoot, fileName)));
    }

    [Fact]
    public void IsTestFile_DetectsByDirectoryName()
    {
        var path = Path.Combine(_tempRoot, "tests", "Helper.cs");

        Assert.True(_service.IsTestFile(path));
    }

    [Fact]
    public void IsTestFile_RegularFileInRegularDirectory_ReturnsFalse()
    {
        var path = Path.Combine(_tempRoot, "src", "Helper.cs");

        Assert.False(_service.IsTestFile(path));
    }

    [Theory]
    [InlineData("MyProject.Tests.csproj", true)]
    [InlineData("MySpec.csproj", true)]
    public void IsTestProject_NameContainsTestOrSpec_ReturnsTrueWithoutReadingFile(string fileName, bool expected)
    {
        // File does not need to exist on disk — name-based check short-circuits before XML parsing.
        var path = Path.Combine(_tempRoot, fileName);

        Assert.Equal(expected, _service.IsTestProject(path));
    }

    [Fact]
    public void IsTestProject_PlainNameWithXunitReference_ReturnsTrue()
    {
        var path = Path.Combine(_tempRoot, "MyProject.csproj");
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(_service.IsTestProject(path));
    }

    [Fact]
    public void IsTestProject_PlainNameWithoutTestFrameworkReference_ReturnsFalse()
    {
        var path = Path.Combine(_tempRoot, "MyProject.csproj");
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);

        Assert.False(_service.IsTestProject(path));
    }

    [Fact]
    public void IsTestProject_MalformedXml_ReturnsFalseInsteadOfThrowing()
    {
        var path = Path.Combine(_tempRoot, "Broken.csproj");
        File.WriteAllText(path, "<Project><Unclosed>");

        var exception = Record.Exception(() => _service.IsTestProject(path));

        Assert.Null(exception);
        Assert.False(_service.IsTestProject(path));
    }

    [Fact]
    public void NormalizePath_RelativePath_CombinesWithRoot()
    {
        var result = _service.NormalizePath(_tempRoot, "sub/foo.cs");

        Assert.Equal(Path.GetFullPath(Path.Combine(_tempRoot, "sub/foo.cs")), result);
    }

    [Fact]
    public void NormalizePath_AbsolutePath_IsUnaffectedByRoot()
    {
        var absolute = Path.Combine(_tempRoot, "foo.cs");

        var result = _service.NormalizePath(Path.Combine(_tempRoot, "other-root"), absolute);

        Assert.Equal(Path.GetFullPath(absolute), result);
    }

    [Fact]
    public void ToRelativePath_ReturnsPathRelativeToRoot()
    {
        var absolute = Path.Combine(_tempRoot, "src", "Foo.cs");

        var result = _service.ToRelativePath(_tempRoot, absolute);

        Assert.Equal(Path.Combine("src", "Foo.cs"), result);
    }

    [Fact]
    public void EnumerateSupportedSourceFiles_ExcludesConfiguredDirectoriesAndUnsupportedExtensions()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "src"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "node_modules"));
        File.WriteAllText(Path.Combine(_tempRoot, "src", "Foo.cs"), "class Foo {}");
        File.WriteAllText(Path.Combine(_tempRoot, "src", "readme.md"), "# readme");
        File.WriteAllText(Path.Combine(_tempRoot, "node_modules", "Excluded.js"), "// excluded");

        var files = _service.EnumerateSupportedSourceFiles(_tempRoot);

        Assert.Single(files);
        Assert.EndsWith("Foo.cs", files[0]);
    }
}

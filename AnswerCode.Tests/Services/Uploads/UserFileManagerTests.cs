using System.Security.Claims;
using AnswerCode.Services;
using AnswerCode.Services.Uploads;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AnswerCode.Tests.Services.Uploads;

public class UserFileManagerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("AnswerCodeManage_").FullName;
    private readonly string _userRoot;
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim(ClaimTypes.Email, "a@example.com")], "test"));

    public UserFileManagerTests()
    {
        _userRoot = Directory.CreateDirectory(Path.Combine(_root, "users", "a@example.com")).FullName;
        Directory.CreateDirectory(Path.Combine(_userRoot, "proj", "src", "sub"));
        File.WriteAllText(Path.Combine(_userRoot, "proj", ".project-meta.json"), "{}");
        File.WriteAllText(Path.Combine(_userRoot, "proj", "README.md"), "hi");
        File.WriteAllText(Path.Combine(_userRoot, "proj", "src", "A.cs"), "class A {}");
        Directory.CreateDirectory(Path.Combine(_root, "users", "b@example.com", "other"));
        File.WriteAllText(Path.Combine(_root, "users", "b@example.com", "other", "B.cs"), "class B {}");
    }

    [Fact]
    public void GetTree_ReturnsNestedDirectoriesOfOwnProjects()
    {
        IReadOnlyList<ManagedProjectNode> tree = CreateManager().GetTree(_user);

        ManagedProjectNode project = Assert.Single(tree);
        Assert.Equal("proj", project.FolderId);
        ManagedTreeNode src = Assert.Single(project.Children);
        Assert.Equal("src", src.Path);
        Assert.Equal("src/sub", Assert.Single(src.Children).Path);
    }

    [Fact]
    public void ListEntries_ProjectRoot_HidesMetadataAndListsDirectoriesFirst()
    {
        ManagedListResult result = CreateManager().ListEntries(_user, "proj", null);

        Assert.Equal(ManagedFileStatus.Success, result.Status);
        Assert.Collection(
            result.Entries,
            entry => Assert.True(entry.IsDirectory && entry.Path == "src"),
            entry => Assert.True(!entry.IsDirectory && entry.Path == "README.md"));
    }

    [Theory]
    [InlineData("proj", "../../b@example.com/other")]
    [InlineData("proj", "..")]
    [InlineData("..", "")]
    [InlineData("users", "")]
    [InlineData("proj", "C:\\Windows")]
    public void ListEntries_PathOutsideProject_ReturnsInvalidPath(string folderId, string path)
    {
        ManagedListResult result = CreateManager().ListEntries(_user, folderId, path);

        Assert.Equal(ManagedFileStatus.InvalidPath, result.Status);
    }

    [Fact]
    public void DeleteFile_RemovesOnlyTargetFile()
    {
        ManagedFileStatus status = CreateManager().DeleteFile(_user, "proj", "src/A.cs");

        Assert.Equal(ManagedFileStatus.Success, status);
        Assert.False(File.Exists(Path.Combine(_userRoot, "proj", "src", "A.cs")));
        Assert.True(File.Exists(Path.Combine(_userRoot, "proj", "README.md")));
    }

    [Fact]
    public void DeleteFile_Metadata_ReturnsInvalidPath()
    {
        ManagedFileStatus status = CreateManager().DeleteFile(_user, "proj", ".project-meta.json");

        Assert.Equal(ManagedFileStatus.InvalidPath, status);
        Assert.True(File.Exists(Path.Combine(_userRoot, "proj", ".project-meta.json")));
    }

    [Fact]
    public void DeleteFile_OtherUsersFile_IsNotDeleted()
    {
        ManagedFileStatus status = CreateManager().DeleteFile(_user, "proj", "../../b@example.com/other/B.cs");

        Assert.Equal(ManagedFileStatus.InvalidPath, status);
        Assert.True(File.Exists(Path.Combine(_root, "users", "b@example.com", "other", "B.cs")));
    }

    [Fact]
    public void DeleteFolder_Subfolder_RemovesSubfolderOnly()
    {
        ManagedFileStatus status = CreateManager().DeleteFolder(_user, "proj", "src");

        Assert.Equal(ManagedFileStatus.Success, status);
        Assert.False(Directory.Exists(Path.Combine(_userRoot, "proj", "src")));
        Assert.True(Directory.Exists(Path.Combine(_userRoot, "proj")));
    }

    [Fact]
    public void DeleteFolder_EmptyPath_RemovesProject()
    {
        ManagedFileStatus status = CreateManager().DeleteFolder(_user, "proj", null);

        Assert.Equal(ManagedFileStatus.Success, status);
        Assert.False(Directory.Exists(Path.Combine(_userRoot, "proj")));
        Assert.True(Directory.Exists(_userRoot));
    }

    [Fact]
    public void DeleteFolder_MissingFolder_ReturnsNotFound()
    {
        ManagedFileStatus status = CreateManager().DeleteFolder(_user, "proj", "missing");

        Assert.Equal(ManagedFileStatus.NotFound, status);
    }

    [Fact]
    public void Rename_File_MovesWithinSameDirectory()
    {
        ManagedFileStatus status = CreateManager().Rename(_user, "proj", "src/A.cs", "B.cs");

        Assert.Equal(ManagedFileStatus.Success, status);
        Assert.False(File.Exists(Path.Combine(_userRoot, "proj", "src", "A.cs")));
        Assert.True(File.Exists(Path.Combine(_userRoot, "proj", "src", "B.cs")));
    }

    [Fact]
    public void Rename_Folder_KeepsContents()
    {
        ManagedFileStatus status = CreateManager().Rename(_user, "proj", "src", "lib");

        Assert.Equal(ManagedFileStatus.Success, status);
        Assert.True(File.Exists(Path.Combine(_userRoot, "proj", "lib", "A.cs")));
    }

    [Theory]
    [InlineData("src/A.cs", "../A.cs")]
    [InlineData("src/A.cs", "..")]
    [InlineData("src/A.cs", "sub/A.cs")]
    [InlineData("src/A.cs", "A.exe")]
    [InlineData("src/A.cs", ".project-meta.json")]
    [InlineData(".project-meta.json", "meta.json")]
    [InlineData("", "x")]
    public void Rename_InvalidInput_ReturnsInvalidPath(string path, string newName)
    {
        ManagedFileStatus status = CreateManager().Rename(_user, "proj", path, newName);

        Assert.Equal(ManagedFileStatus.InvalidPath, status);
    }

    [Fact]
    public void Rename_TargetExists_ReturnsConflict()
    {
        ManagedFileStatus status = CreateManager().Rename(_user, "proj", "src", "README.md");

        Assert.Equal(ManagedFileStatus.Conflict, status);
        Assert.True(Directory.Exists(Path.Combine(_userRoot, "proj", "src")));
    }

    [Fact]
    public void CreateFolder_ProjectRoot_CreatesEmptyDirectory()
    {
        ManagedFileStatus status = CreateManager().CreateFolder(_user, "proj", null, "docs");

        Assert.Equal(ManagedFileStatus.Success, status);
        string created = Path.Combine(_userRoot, "proj", "docs");
        Assert.True(Directory.Exists(created));
        Assert.Empty(Directory.GetFileSystemEntries(created));
    }

    [Fact]
    public void CreateFolder_Subdirectory_CreatesNestedDirectory()
    {
        ManagedFileStatus status = CreateManager().CreateFolder(_user, "proj", "src", "  helpers  ");

        Assert.Equal(ManagedFileStatus.Success, status);
        Assert.True(Directory.Exists(Path.Combine(_userRoot, "proj", "src", "helpers")));
        Assert.True(File.Exists(Path.Combine(_userRoot, "proj", "src", "A.cs")));
    }

    [Theory]
    [InlineData("proj", "", "")]
    [InlineData("proj", "", "   ")]
    [InlineData("proj", "", ".")]
    [InlineData("proj", "", "..")]
    [InlineData("proj", "", "a/b")]
    [InlineData("proj", "", "a\\b")]
    [InlineData("proj", "", ".project-meta.json")]
    [InlineData("proj", "../../b@example.com/other", "x")]
    [InlineData("..", "", "x")]
    [InlineData("proj", "C:\\Windows", "x")]
    public void CreateFolder_InvalidInput_ReturnsInvalidPath(string folderId, string path, string name)
    {
        ManagedFileStatus status = CreateManager().CreateFolder(_user, folderId, path, name);

        Assert.Equal(ManagedFileStatus.InvalidPath, status);
        Assert.False(Directory.Exists(Path.Combine(_userRoot, "proj", "x")));
    }

    [Theory]
    [InlineData(null, "src")]
    [InlineData("src", "sub")]
    [InlineData("", "README.md")]
    public void CreateFolder_TargetExists_ReturnsConflict(string? path, string name)
    {
        ManagedFileStatus status = CreateManager().CreateFolder(_user, "proj", path, name);

        Assert.Equal(ManagedFileStatus.Conflict, status);
        Assert.True(File.Exists(Path.Combine(_userRoot, "proj", "README.md")));
        Assert.True(Directory.Exists(Path.Combine(_userRoot, "proj", "src", "sub")));
    }

    [Fact]
    public void CreateFolder_MissingParent_ReturnsNotFound()
    {
        ManagedFileStatus status = CreateManager().CreateFolder(_user, "proj", "missing", "child");

        Assert.Equal(ManagedFileStatus.NotFound, status);
        Assert.False(Directory.Exists(Path.Combine(_userRoot, "proj", "missing")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private UserFileManager CreateManager()
    {
        var storage = new Mock<IUserStorageService>();
        storage.Setup(item => item.GetUserStoragePath(It.IsAny<ClaimsPrincipal>())).Returns(_userRoot);
        var uploads = new Mock<ISourceUploadService>();
        uploads.Setup(item => item.ListUserProjects(It.IsAny<ClaimsPrincipal>()))
            .Returns([new SourceProjectSummary("proj", "Project", 2, 0, DateTime.UtcNow)]);
        return new UserFileManager(
            storage.Object,
            uploads.Object,
            new SourceFilePolicy(),
            NullLogger<UserFileManager>.Instance);
    }
}

using AnswerCode.Services;
using AnswerCode.Services.Tools;

namespace AnswerCode.Tests.Services;

public class ToolResultFormatterTests
{
    private const string _rootPath = "C:/repo";

    // ── FormatToolCallSummary ───────────────────────────────────────────

    [Fact]
    public void FormatToolCallSummary_Grep_IncludesPatternAndInclude()
    {
        var args = """{"pattern": "TODO", "include": "*.cs"}""";

        var summary = ToolResultFormatter.FormatToolCallSummary(GrepTool.ToolName, args, _rootPath);

        Assert.Equal("pattern=TODO  include=*.cs", summary);
    }

    [Fact]
    public void FormatToolCallSummary_ReadFile_ExtractsFileNameOnly()
    {
        var args = """{"file_path": "src/Foo.cs"}""";

        var summary = ToolResultFormatter.FormatToolCallSummary(ReadFileTool.ToolName, args, _rootPath);

        Assert.Equal("Foo.cs", summary);
    }

    [Fact]
    public void FormatToolCallSummary_ListDirectory_NoPath_ReturnsProjectRootLabel()
    {
        var args = "{}";

        var summary = ToolResultFormatter.FormatToolCallSummary(ListDirectoryTool.ToolName, args, _rootPath);

        Assert.Equal("(project root)", summary);
    }

    [Fact]
    public void FormatToolCallSummary_RepoMap_NoScope_ReturnsFullRepositoryLabel()
    {
        var summary = ToolResultFormatter.FormatToolCallSummary(RepoMapTool.ToolName, "{}", _rootPath);

        Assert.Equal("(full repository)", summary);
    }

    [Fact]
    public void FormatToolCallSummary_MalformedJson_FallsBackToRawArgs()
    {
        var args = "not json";

        var summary = ToolResultFormatter.FormatToolCallSummary(GrepTool.ToolName, args, _rootPath);

        Assert.Equal(args, summary);
    }

    [Fact]
    public void FormatToolCallSummary_UnknownTool_TruncatesLongArgs()
    {
        var args = new string('a', 150);

        var summary = ToolResultFormatter.FormatToolCallSummary("some_unknown_tool", $"\"{args}\"", _rootPath);

        Assert.True(summary.Length <= 103);
        Assert.EndsWith("...", summary);
    }

    // ── FormatToolResultSummary ─────────────────────────────────────────

    [Fact]
    public void FormatToolResultSummary_Grep_NoMatches_ReturnsNoMatches()
    {
        var summary = ToolResultFormatter.FormatToolResultSummary(GrepTool.ToolName, "No matches found for pattern 'xyz'");

        Assert.Equal("no matches", summary);
    }

    [Fact]
    public void FormatToolResultSummary_Grep_CountsMatchesAndFiles()
    {
        var result = "Found 3 matches:\nfile1.cs:\n  1: foo\n  2: foo again\nfile2.cs:\n  5: bar\n";

        var summary = ToolResultFormatter.FormatToolResultSummary(GrepTool.ToolName, result);

        Assert.Equal("3 matches in 2 files", summary);
    }

    [Fact]
    public void FormatToolResultSummary_Grep_ErrorResult_ReturnsEmpty()
    {
        var summary = ToolResultFormatter.FormatToolResultSummary(GrepTool.ToolName, "Error: invalid regex");

        Assert.Equal("", summary);
    }

    [Fact]
    public void FormatToolResultSummary_EmptyResult_ReturnsEmpty()
    {
        var summary = ToolResultFormatter.FormatToolResultSummary(GrepTool.ToolName, "");

        Assert.Equal("", summary);
    }

    [Fact]
    public void FormatToolResultSummary_Glob_NoFiles_ReturnsNoFilesFound()
    {
        var summary = ToolResultFormatter.FormatToolResultSummary(GlobTool.ToolName, "No files found matching '*.xyz'");

        Assert.Equal("no files found", summary);
    }

    [Fact]
    public void FormatToolResultSummary_Glob_CountsFiles()
    {
        var summary = ToolResultFormatter.FormatToolResultSummary(GlobTool.ToolName, "Found 5 files:\na.cs\nb.cs");

        Assert.Equal("5 files found", summary);
    }

    [Fact]
    public void FormatToolResultSummary_ReadFile_ExtractsFileNameAndLineCount()
    {
        var result = "File: Foo.cs (120 total lines)\n...content...";

        var summary = ToolResultFormatter.FormatToolResultSummary(ReadFileTool.ToolName, result);

        Assert.Equal("Foo.cs (120 lines)", summary);
    }

    [Fact]
    public void FormatToolResultSummary_ListDirectory_CountsNonHeaderLines()
    {
        var result = "Directory: /root\nfile1.cs\nfile2.cs\nsubdir/";

        var summary = ToolResultFormatter.FormatToolResultSummary(ListDirectoryTool.ToolName, result);

        Assert.Equal("3 items", summary);
    }

    [Fact]
    public void FormatToolResultSummary_FindDefinition_NoDefinitions_ReturnsFirstLine()
    {
        var summary = ToolResultFormatter.FormatToolResultSummary(FindDefinitionTool.ToolName, "No definitions found for 'Foo'");

        Assert.Equal("No definitions found for 'Foo'", summary);
    }

    [Fact]
    public void FormatToolResultSummary_FindDefinition_IncludesCountAndSignature()
    {
        var result = "Found 2 definition(s) for 'Foo':\n  public class Foo\n  public class Foo2";

        var summary = ToolResultFormatter.FormatToolResultSummary(FindDefinitionTool.ToolName, result);

        Assert.Equal("2 definition(s) for 'Foo': public class Foo", summary);
    }

    [Fact]
    public void FormatToolResultSummary_UnrecognizedTool_ReturnsEmpty()
    {
        var summary = ToolResultFormatter.FormatToolResultSummary("some_unknown_tool", "anything");

        Assert.Equal("", summary);
    }
}

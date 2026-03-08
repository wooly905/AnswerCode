namespace AnswerCode.Services.Analysis;

public interface IWorkspaceFileService
{
    IReadOnlyList<string> EnumerateCSharpFiles(string rootPath);

    IReadOnlyList<string> EnumerateSupportedSourceFiles(string rootPath);

    IReadOnlyList<string> EnumerateProjectFiles(string rootPath, string searchPattern);

    string NormalizePath(string rootPath, string path);

    string ToRelativePath(string rootPath, string path);

    string? GetLanguageId(string filePath);

    bool IsTestFile(string filePath);

    bool IsTestProject(string projectFilePath);

    bool IsExcludedDirectory(string directoryName);
}

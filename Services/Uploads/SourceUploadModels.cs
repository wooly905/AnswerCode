namespace AnswerCode.Services.Uploads;

public sealed record SourceUploadResult(
    string FolderId,
    string DisplayName,
    int FileCount,
    long TotalSizeBytes,
    double TotalSizeMB,
    bool Persistent,
    string? DeleteToken);

public sealed record SourceProjectSummary(
    string FolderId,
    string DisplayName,
    int FileCount,
    double SizeMB,
    DateTime CreatedAt);

public enum SourceDeleteStatus
{
    Success,
    InvalidFolderId,
    Unauthorized,
    NotFound,
    Failed
}

public sealed record SourceDeleteResult(SourceDeleteStatus Status, string Message, string FolderId);

public sealed class SourceUploadException(string message) : Exception(message);
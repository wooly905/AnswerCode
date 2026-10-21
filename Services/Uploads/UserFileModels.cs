namespace AnswerCode.Services.Uploads;

public sealed record ManagedTreeNode(string Name, string Path, IReadOnlyList<ManagedTreeNode> Children);

public sealed record ManagedProjectNode(string FolderId, string DisplayName, IReadOnlyList<ManagedTreeNode> Children);

public sealed record ManagedEntry(string Name, string Path, bool IsDirectory, long SizeBytes, DateTime ModifiedUtc);

public enum ManagedFileStatus
{
    Success,
    InvalidPath,
    NotFound,
    Conflict,
    Failed
}

public sealed record ManagedListResult(ManagedFileStatus Status, IReadOnlyList<ManagedEntry> Entries);

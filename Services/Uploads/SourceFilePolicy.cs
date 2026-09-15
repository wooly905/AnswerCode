using Microsoft.AspNetCore.Http;

namespace AnswerCode.Services.Uploads;

public sealed class SourceFilePolicy : ISourceFilePolicy
{
    private static readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb",
        ".js", ".ts", ".jsx", ".tsx",
        ".py", ".go", ".rs", ".java",
        ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx",
        ".htm", ".html", ".css", ".md", ".json", ".xml", ".toml", ".yml", ".yaml", ".sql",
        ".csproj", ".fsproj", ".vbproj", ".sln"
    };

    private static readonly HashSet<string> _allowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "package-lock.json", "requirements.txt", "pyproject.toml",
        "go.mod", "go.sum", "Cargo.toml", "Cargo.lock", "pom.xml", "build.gradle",
        "CMakeLists.txt", "Dockerfile", ".gitignore", ".editorconfig", "Makefile"
    };

    private static readonly HashSet<string> _ignoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".vscode", ".idea",
        "dist", "build", "out", "target", "__pycache__", "venv", ".venv"
    };

    public bool IsAllowed(string relativePath)
    {
        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(_ignoredDirectories.Contains))
        {
            return false;
        }

        string fileName = Path.GetFileName(relativePath);
        return _allowedFiles.Contains(fileName) || _allowedExtensions.Contains(Path.GetExtension(fileName));
    }

    public string? ResolveDestinationPath(string destinationRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        // Normalize both separators so Windows-style paths are rejected on Linux CI too.
        string normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (IsUnsafeRelativePath(relativePath, normalized))
        {
            return null;
        }

        string root = Path.GetFullPath(destinationRoot);
        string destination = Path.GetFullPath(Path.Combine(root, normalized));
        return destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? destination
            : null;
    }

    private static bool IsUnsafeRelativePath(string original, string normalized)
    {
        if (Path.IsPathRooted(normalized)
            || normalized.StartsWith(Path.DirectorySeparatorChar)
            || normalized.StartsWith(Path.AltDirectorySeparatorChar))
        {
            return true;
        }

        // Drive-letter absolute paths (C:\...) are not rooted on Unix, but must still be rejected.
        if (original.Length >= 2 && char.IsAsciiLetter(original[0]) && original[1] == ':')
        {
            return true;
        }

        return normalized
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Contains("..");
    }

    public async Task<bool> IsSafeContentAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file.Length < 4)
        {
            return true;
        }

        await using Stream stream = file.OpenReadStream();
        byte[] buffer = new byte[4];
        int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (bytesRead < 2)
        {
            return true;
        }

        if (buffer[0] == 0x4D && buffer[1] == 0x5A)
        {
            return false;
        }

        return bytesRead < 4 || !IsExecutableSignature(buffer);
    }

    private static bool IsExecutableSignature(byte[] buffer) =>
        (buffer[0] == 0x7F && buffer[1] == 0x45 && buffer[2] == 0x4C && buffer[3] == 0x46)
        || (buffer[0] == 0xFE && buffer[1] == 0xED && buffer[2] == 0xFA && buffer[3] == 0xCE)
        || (buffer[0] == 0xCE && buffer[1] == 0xFA && buffer[2] == 0xED && buffer[3] == 0xFE)
        || (buffer[0] == 0xFE && buffer[1] == 0xED && buffer[2] == 0xFA && buffer[3] == 0xCF)
        || (buffer[0] == 0xCF && buffer[1] == 0xFA && buffer[2] == 0xED && buffer[3] == 0xFE)
        || (buffer[0] == 0xCA && buffer[1] == 0xFE && buffer[2] == 0xBA && buffer[3] == 0xBE);
}
using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// List directory tool — shows directory tree structure
/// </summary>
public class ListDirectoryTool : ITool
{
    public string Name => "list_directory";

    public string Description =>
        "List the contents of a directory as a tree structure. " +
        "Shows subdirectories and code files. Use 'max_depth' to control traversal depth (default: 3). " +
        "Path can be absolute or relative to the project root. " +
        "Use this tool first to understand the project structure before searching for specific code.";

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "packages", ".git", ".svn", ".hg",
        ".vs", ".vscode", ".idea", "dist", "build", "out", "target",
        "__pycache__", ".pytest_cache", ".mypy_cache", "venv", "env",
        "vendor", "bower_components", ".nuget"
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".vb", ".fs", ".fsx",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
        ".py", ".pyw", ".pyi",
        ".java", ".kt", ".kts", ".scala",
        ".go", ".rs", ".c", ".cpp", ".h", ".hpp",
        ".rb", ".php", ".swift", ".m", ".mm",
        ".sql", ".graphql", ".gql",
        ".json", ".xml", ".yaml", ".yml", ".toml",
        ".css", ".scss", ".sass", ".less",
        ".html", ".htm", ".cshtml", ".razor",
        ".sh", ".bash", ".ps1", ".psm1", ".bat", ".cmd",
        ".csproj", ".sln", ".props", ".targets"
    };

    public ChatTool GetChatToolDefinition()
    {
        return ChatTool.CreateFunctionTool(
            functionName: Name,
            functionDescription: Description,
            functionParameters: BinaryData.FromObjectAsJson(new
            {
                type = "object",
                properties = new
                {
                    path = new
                    {
                        type = "string",
                        description = "Directory path to list (absolute or relative to project root). Defaults to project root if omitted."
                    },
                    max_depth = new
                    {
                        type = "integer",
                        description = "Maximum depth to traverse (default: 3)"
                    }
                },
                required = Array.Empty<string>()
            }));
    }

    public Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        int maxDepth = args.TryGetProperty("max_depth", out var d) ? d.GetInt32() : 3;

        var dirPath = string.IsNullOrWhiteSpace(path)
            ? context.RootPath
            : Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(context.RootPath, path));

        if (!Directory.Exists(dirPath))
            return Task.FromResult($"Error: Directory not found: {dirPath}");

        var sb = new StringBuilder();
        var relRoot = Path.GetRelativePath(context.RootPath, dirPath);
        sb.AppendLine($"Directory: {(relRoot == "." ? Path.GetFileName(context.RootPath) : relRoot)}");
        sb.AppendLine();

        BuildTree(new DirectoryInfo(dirPath), sb, "", maxDepth, 0);

        return Task.FromResult(sb.ToString());
    }

    private static void BuildTree(DirectoryInfo dir, StringBuilder sb, string indent, int maxDepth, int currentDepth)
    {
        if (currentDepth >= maxDepth || ExcludedDirectories.Contains(dir.Name))
            return;

        try
        {
            var subDirs = dir.GetDirectories()
                .Where(d => !ExcludedDirectories.Contains(d.Name))
                .OrderBy(d => d.Name)
                .ToList();

            var files = dir.GetFiles()
                .Where(f => CodeExtensions.Contains(f.Extension))
                .OrderBy(f => f.Name)
                .ToList();

            foreach (var subDir in subDirs)
            {
                sb.AppendLine($"{indent}📁 {subDir.Name}/");
                BuildTree(subDir, sb, indent + "  ", maxDepth, currentDepth + 1);
            }

            foreach (var file in files.Take(30))
            {
                sb.AppendLine($"{indent}  📄 {file.Name}");
            }

            if (files.Count > 30)
            {
                sb.AppendLine($"{indent}  ... and {files.Count - 30} more files");
            }
        }
        catch (UnauthorizedAccessException)
        {
            sb.AppendLine($"{indent}  [Access Denied]");
        }
    }
}

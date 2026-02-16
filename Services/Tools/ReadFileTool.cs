using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Read file tool — reads file contents with line numbers
/// </summary>
public class ReadFileTool : ITool
{
    public const string ToolName = "read_file";

    public string Name => ToolName;

    public string Description =>
        "Read the contents of a file. Returns file content with line numbers. " +
        "Use the 'offset' parameter to start reading from a specific line (0-based). " +
        "Use the 'max_lines' parameter to limit how many lines to read (default: 500). " +
        "Use this tool after grep_search to read the full context of matching files. " +
        "File path can be absolute or relative to the project root.";

    private const int DefaultMaxLines = 500;
    private const int MaxLineLength = 2000;
    private const int MaxBytes = 50 * 1024; // 50 KB

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z",
        ".avi",
        ".bin",
        ".bmp",
        ".dll",
        ".doc",
        ".docx",
        ".eot",
        ".exe",
        ".flac",
        ".gif",
        ".gz",
        ".ico",
        ".jpeg",
        ".jpg",
        ".mov",
        ".mp3",
        ".mp4",
        ".obj",
        ".otf",
        ".pdb",
        ".pdf",
        ".png",
        ".ppt",
        ".pptx",
        ".rar",
        ".svg",
        ".tar",
        ".ttf",
        ".wav",
        ".webp",
        ".woff",
        ".woff2",
        ".xls",
        ".xlsx",
        ".zip"
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
                    file_path = new
                    {
                        type = "string",
                        description = "Path to the file to read (absolute or relative to project root)"
                    },
                    offset = new
                    {
                        type = "integer",
                        description = "Line offset to start reading from (0-based, default: 0)"
                    },
                    max_lines = new
                    {
                        type = "integer",
                        description = "Maximum number of lines to read (default: 500)"
                    }
                },
                required = new[] { "file_path" }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var filePath = args.GetProperty("file_path").GetString() ?? "";
        int offset = args.TryGetProperty("offset", out var off) ? off.GetInt32() : 0;
        int maxLines = args.TryGetProperty("max_lines", out var ml) ? ml.GetInt32() : DefaultMaxLines;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: file_path is required";
        }

        // Resolve relative paths
        if (!Path.IsPathRooted(filePath))
        {
            filePath = Path.GetFullPath(Path.Combine(context.RootPath, filePath));
        }

        if (!File.Exists(filePath))
        {
            return $"Error: File not found: {filePath}";
        }

        var ext = Path.GetExtension(filePath);
        if (BinaryExtensions.Contains(ext))
        {
            return $"[Binary file: {filePath}]";
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(filePath);
            var sb = new StringBuilder();
            var relPath = Path.GetRelativePath(context.RootPath, filePath);
            sb.AppendLine($"File: {relPath} ({lines.Length} total lines)");
            sb.AppendLine();

            int bytes = 0;
            bool truncatedByBytes = false;
            int end = Math.Min(lines.Length, offset + maxLines);

            for (int i = offset; i < end; i++)
            {
                var line = lines[i].Length > MaxLineLength
                    ? lines[i][..MaxLineLength] + "..."
                    : lines[i];

                var lineStr = $"{(i + 1).ToString().PadLeft(5)}| {line}";
                var size = Encoding.UTF8.GetByteCount(lineStr) + 1;

                if (bytes + size > MaxBytes)
                {
                    truncatedByBytes = true;
                    break;
                }

                sb.AppendLine(lineStr);
                bytes += size;
            }

            if (truncatedByBytes)
            {
                sb.AppendLine($"\n(Output truncated at {MaxBytes / 1024}KB. Use 'offset' to read beyond this point.)");
            }
            else if (end < lines.Length)
            {
                sb.AppendLine($"\n(File has {lines.Length - end} more lines. Use offset={end} to continue reading.)");
            }
            else
            {
                sb.AppendLine($"\n(End of file — {lines.Length} total lines)");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AnswerCode.Models;

namespace AnswerCode.Services;

/// <summary>
/// Code Explorer Service Implementation
/// </summary>
public class CodeExplorerService : ICodeExplorerService
{
    private readonly ILogger<CodeExplorerService> _logger;
    private readonly ILLMService _llmService;
    private const int MaxLineLength = 2000;
    private const int GrepResultLimit = 100;
    private const int GlobResultLimit = 100;

    // Common binary file extensions
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".obj", ".bin", ".zip", ".rar", ".7z", ".tar", ".gz",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp",
        ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flac",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".woff", ".woff2", ".ttf", ".eot", ".otf"
    };

    // Common code file extensions
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".vb", ".fs", ".fsx",           // .NET
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",  // JavaScript/TypeScript
        ".py", ".pyw", ".pyi",                          // Python
        ".java", ".kt", ".kts", ".scala",               // JVM
        ".go", ".rs", ".c", ".cpp", ".h", ".hpp",       // Systems
        ".rb", ".php", ".swift", ".m", ".mm",           // Other languages
        ".sql", ".graphql", ".gql",                     // Query languages
        ".json", ".xml", ".yaml", ".yml", ".toml",      // Config
        ".css", ".scss", ".sass", ".less",              // Styles
        ".html", ".htm", ".cshtml", ".razor",           // Markup
        ".sh", ".bash", ".ps1", ".psm1", ".bat", ".cmd", // Scripts
        ".log", ".txt", ".ini", ".conf", ".config"      // Logs and Configs
    };

    // Documentation file extensions to exclude from search
    private static readonly HashSet<string> DocumentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".rst", ".doc", ".docx", ".pdf", ".rtf"
    };

    // Directories to exclude
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "packages", ".git", ".svn", ".hg",
        ".vs", ".vscode", ".idea", "dist", "build", "out", "target",
        "__pycache__", ".pytest_cache", ".mypy_cache", "venv", "env",
        "vendor", "bower_components", ".nuget"
    };

    public CodeExplorerService(ILogger<CodeExplorerService> logger, ILLMService llmService)
    {
        _logger = logger;
        _llmService = llmService;
    }

    /// <summary>
    /// Search file contents using regex (similar to grep)
    /// </summary>
    private async Task<List<SearchResult>> GrepSearchAsync(string pattern,
                                                           string rootPath,
                                                           bool caseInsensitive = true,
                                                           string? include = null)
    {
        _logger.LogInformation("Grep search (C# Only): pattern='{Pattern}', rootPath='{RootPath}', include='{Include}'", pattern, rootPath, include);

        // Force C# fallback to avoid OS-dependent issues with external tools like ripgrep
        // string? rgPath = FindRipgrep(); 

        var results = new List<SearchResult>();
        var options = caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;

        Regex regex;
        try
        {
            regex = new Regex(pattern, options | RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid regex pattern: {Pattern}, error: {Error}", pattern, ex.Message);
            regex = new Regex(Regex.Escape(pattern), options | RegexOptions.Compiled);
        }

        // Prepare include pattern regex if provided
        Regex? includeRegex = null;
        if (!string.IsNullOrWhiteSpace(include))
        {
            try 
            {
                // Simple glob to regex conversion for filters
                var includePattern = "^" + Regex.Escape(include).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                includeRegex = new Regex(includePattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            catch
            {
                _logger.LogWarning("Invalid include pattern: {Include}, ignoring.", include);
            }
        }

        await Task.Run(() =>
        {
            var files = GetCodeFiles(rootPath);

            foreach (var file in files)
            {
                // Filter by include pattern if specified
                if (includeRegex != null && !includeRegex.IsMatch(Path.GetFileName(file)))
                {
                    continue;
                }

                try
                {
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length && results.Count < GrepResultLimit; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            var lineText = lines[i].Trim();
                            if (lineText.Length > MaxLineLength)
                            {
                                lineText = lineText.Substring(0, MaxLineLength) + "...";
                            }

                            results.Add(new SearchResult
                            {
                                FilePath = file,
                                RelativePath = Path.GetRelativePath(rootPath, file),
                                LineNumber = i + 1,
                                LineContent = lineText
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to read file {File}: {Error}", file, ex.Message);
                }
                
                if (results.Count >= GrepResultLimit) break;
            }
        });

        _logger.LogInformation("C# search found {Count} matches.", results.Count);
        return results;
    }

    /// <summary>
    /// Read file contents
    /// </summary>
    public async Task<string> ReadFileAsync(string filePath, int? maxLines = null, int? offset = null)
    {
        _logger.LogInformation("Reading file: {FilePath}", filePath);

        if (!File.Exists(filePath))
        {
            return $"[Error: File not found: {filePath}]";
        }

        if (IsBinaryFile(filePath) || await IsBinaryContentAsync(filePath))
        {
            return $"[Binary file: {filePath}]";
        }

        try
        {
            var limit = maxLines ?? 1000;
            var start = Math.Max(0, offset ?? 0);

            var raw = new List<string>();
            var bytes = 0;
            var truncatedByBytes = false;
            var lineIndex = 0;
            foreach (var line in File.ReadLines(filePath))
            {
                if (lineIndex++ < start) continue;
                if (raw.Count >= limit) break;

                var trimmedLine = line.Length > MaxLineLength ? line.Substring(0, MaxLineLength) + "..." : line;
                var size = Encoding.UTF8.GetByteCount(trimmedLine) + (raw.Count > 0 ? 1 : 0);
                if (bytes + size > 50 * 1024)
                {
                    truncatedByBytes = true;
                    break;
                }

                raw.Add(trimmedLine);
                bytes += size;
            }

            var content = string.Join(Environment.NewLine, raw);
            if (truncatedByBytes)
            {
                content += $"{Environment.NewLine}{Environment.NewLine}[Output truncated at 50KB]";
            }

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to read file {File}: {Error}", filePath, ex.Message);
            return $"[Error reading file: {ex.Message}]";
        }
    }

    /// <summary>
    /// Get project structure overview
    /// </summary>
    public async Task<string> GetProjectStructureAsync(string rootPath, int maxDepth = 6)
    {
        _logger.LogInformation("Getting project structure: {RootPath}", rootPath);

        return await Task.Run(() =>
        {
            StringBuilder sb = new();
            sb.AppendLine($"Project: {Path.GetFileName(rootPath)}");
            sb.AppendLine("```");
            BuildDirectoryTree(new DirectoryInfo(rootPath), sb, "", maxDepth, 0);
            sb.AppendLine("```");
            return sb.ToString();
        });
    }

    /// <summary>
    /// Intelligently search relevant code based on the question
    /// </summary>
    public async Task<CodeContext> ExploreCodebaseAsync(string question, string rootPath, string? modelProvider = null)
    {
        _logger.LogInformation("Exploring codebase for question: {Question}", question);

        string projectStructure = await GetProjectStructureAsync(rootPath, 2);
        //_logger.LogInformation("Project structure = {structure}.", projectStructure);

        CodeContext context = new()
        {
            // 1. Get project structure
            ProjectStructure = projectStructure
        };

        // 2. Extract keywords from the question
        List<string> keywords = await _llmService.ExtractKeywordsAsync(question, modelProvider);
        _logger.LogInformation("Extracted keywords: {Keywords}", string.Join(", ", keywords));

        // 3. Search for each keyword
        List<SearchResult> allSearchResults = [];
        HashSet<string> relevantFiles = [];

        foreach (string? keyword in keywords.Take(10))
        {
            // Grep search
            List<SearchResult> grepResults = await GrepSearchAsync(keyword, rootPath);
            allSearchResults.AddRange(grepResults);

            // Collect relevant files from search results
            foreach (SearchResult? result in grepResults.Take(30))
            {
                relevantFiles.Add(result.FilePath);
            }
        }

        // 4. Deduplicate (distinct) and limit search results
        context.SearchResults = allSearchResults
            .GroupBy(r => new { r.FilePath, r.LineNumber })
            .Select(g => g.First())
            .OrderByDescending(r => GetFileMtimeUtc(r.FilePath))
            .ThenBy(r => r.FilePath)
            .ThenBy(r => r.LineNumber)
            .Take(50)
            .ToList();

        // 5. Read most relevant file contents
        var filesToRead = relevantFiles.Take(30).ToList();
        _logger.LogInformation("Found {Count} relevant files to read: {Files}", 
            filesToRead.Count, 
            string.Join(", ", filesToRead.Select(f => Path.GetRelativePath(rootPath, f))));

        foreach (string filePath in filesToRead)
        {
            context.FileContents[filePath] = await ReadFileAsync(filePath, 2000);
        }

        return context;
    }

    #region Private Methods

    private static bool IsBinaryFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return BinaryExtensions.Contains(extension);
    }

    private static bool IsDocumentationFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return DocumentationExtensions.Contains(extension);
    }

    private static async Task<bool> IsBinaryContentAsync(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0) return false;

            var bufferSize = (int)Math.Min(4096, fileInfo.Length);
            var buffer = new byte[bufferSize];
            using var stream = File.OpenRead(filePath);
            var read = await stream.ReadAsync(buffer, 0, bufferSize);
            if (read == 0) return false;

            var nonPrintableCount = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0) return true;
                if (buffer[i] < 9 || (buffer[i] > 13 && buffer[i] < 32))
                {
                    nonPrintableCount++;
                }
            }

            return nonPrintableCount / (double)read > 0.3;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime GetFileMtimeUtc(string filePath)
    {
        try
        {
            return File.GetLastWriteTimeUtc(filePath);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string? FindRipgrep()
    {
        string rgName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "rg.exe" : "rg";
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, rgName);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<List<SearchResult>> RunRipgrepSearchAsync(string rgPath,
                                                                 string pattern,
                                                                 string rootPath,
                                                                 bool caseInsensitive,
                                                                 string? include)
    {
        var args = new List<string>
        {
            "-nH",
            "--hidden",
            "--follow",
            "--no-messages",
            "--field-match-separator=|",
            "--regexp",
            pattern
        };

        if (caseInsensitive)
        {
            args.Add("-i");
        }

        if (!string.IsNullOrWhiteSpace(include))
        {
            args.Add("--glob");
            args.Add(include);
        }

        foreach (var ext in DocumentationExtensions)
        {
            args.Add("--glob");
            args.Add($"!*{ext}");
        }

        args.Add(rootPath);

        (int ExitCode, string StandardOutput, string StandardError) output = await RunProcessAsync(rgPath, args);

        if (output.ExitCode == 1 || (output.ExitCode == 2 && string.IsNullOrWhiteSpace(output.StandardOutput)))
        {
            return [];
        }

        if (output.ExitCode != 0 && output.ExitCode != 2)
        {
            throw new InvalidOperationException($"ripgrep failed: {output.StandardError}");
        }

        List<(SearchResult Result, DateTime Mtime)> matches = new List<(SearchResult Result, DateTime Mtime)>();
        string[] lines = output.StandardOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            var firstSep = line.IndexOf('|');
            if (firstSep <= 0)
            {
                continue;
            }

            var secondSep = line.IndexOf('|', firstSep + 1);
            if (secondSep <= firstSep)
            {
                continue;
            }

            var filePath = line.Substring(0, firstSep);
            var lineNumStr = line.Substring(firstSep + 1, secondSep - firstSep - 1);
            var lineText = line.Substring(secondSep + 1);
            if (!int.TryParse(lineNumStr, out var lineNum))
            {
                continue;
            }

            if (lineText.Length > MaxLineLength)
            {
                lineText = lineText.Substring(0, MaxLineLength) + "...";
            }

            matches.Add((
                new SearchResult
                {
                    FilePath = filePath,
                    RelativePath = Path.GetRelativePath(rootPath, filePath),
                    LineNumber = lineNum,
                    LineContent = lineText
                },
                GetFileMtimeUtc(filePath)
            ));
        }

        return matches
            .OrderByDescending(m => m.Mtime)
            .Take(GrepResultLimit)
            .Select(m => m.Result)
            .ToList();
    }

    private async Task<List<string>> RunRipgrepGlobAsync(string rgPath, string pattern, string rootPath)
    {
        var args = new List<string>
        {
            "--files",
            "--hidden",
            "--follow",
            "--glob",
            "!.git/*",
        };

        foreach (var ext in DocumentationExtensions)
        {
            args.Add("--glob");
            args.Add($"!*{ext}");
        }

        args.Add("--glob");
        args.Add(pattern);

        var output = await RunProcessAsync(rgPath, args, rootPath);
        if (output.ExitCode != 0)
        {
            return new List<string>();
        }

        var files = new List<(string Path, DateTime Mtime)>();
        var lines = output.StandardOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, line));
            files.Add((fullPath, GetFileMtimeUtc(fullPath)));
        }

        return files
            .OrderByDescending(f => f.Mtime)
            .Take(GlobResultLimit)
            .Select(f => f.Path)
            .ToList();
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
        string fileName,
        List<string> args,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return (-1, "", "Failed to start process");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private IEnumerable<string> GetCodeFiles(string rootPath)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(rootPath));

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // Skip excluded directories
            if (ExcludedDirectories.Contains(dir.Name))
                continue;

            FileInfo[] files;
            try
            {
                files = dir.GetFiles();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var ext = file.Extension;
                if (CodeExtensions.Contains(ext) && !IsBinaryFile(file.FullName))
                {
                    yield return file.FullName;
                }
            }

            try
            {
                foreach (var subDir in dir.GetDirectories())
                {
                    if (!ExcludedDirectories.Contains(subDir.Name))
                    {
                        stack.Push(subDir);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore directories without access permission
            }
        }
    }

    private void BuildDirectoryTree(DirectoryInfo dir, StringBuilder sb, string indent, int maxDepth, int currentDepth)
    {
        if (currentDepth >= maxDepth || ExcludedDirectories.Contains(dir.Name))
        {
            return;
        }

        try
        {
            List<DirectoryInfo> subDirs = [.. dir.GetDirectories().Where(d => !ExcludedDirectories.Contains(d.Name)).OrderBy(d => d.Name)];

            List<FileInfo> files = [.. dir.GetFiles().Where(f => CodeExtensions.Contains(f.Extension)).OrderBy(f => f.Name)];

            foreach (DirectoryInfo subDir in subDirs)
            {
                sb.AppendLine($"{indent}📁 {subDir.Name}/");
                BuildDirectoryTree(subDir, sb, indent + "  ", maxDepth, currentDepth + 1);
            }

            foreach (FileInfo? file in files.Take(20)) // Limit files displayed per directory
            {
                sb.AppendLine($"{indent}  📄 {file.Name}");
            }

            if (files.Count > 20)
            {
                sb.AppendLine($"{indent}  ... and {files.Count - 20} more files");
            }
        }
        catch (UnauthorizedAccessException)
        {
            sb.AppendLine($"{indent}  [Access Denied]");
        }
    }

    #endregion
}

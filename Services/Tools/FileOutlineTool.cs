using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI.Chat;

namespace AnswerCode.Services.Tools;

/// <summary>
/// File outline tool — extracts structural outline (classes, methods, properties, etc.)
/// with line numbers but WITHOUT implementation bodies. Much more token-efficient than
/// reading the full file when you just need to understand the file's structure.
/// </summary>
public class FileOutlineTool : ITool
{
    public const string ToolName = "get_file_outline";

    public string Name => ToolName;

    public string Description =>
        "Get the structural outline of a code file — classes, methods, properties, interfaces, " +
        "enums, and functions with their line numbers and signatures. " +
        "Does NOT return implementation bodies, making it much more token-efficient than read_file " +
        "when you need to understand a file's structure. " +
        "Use this before read_file to know exactly which lines to read.";

    private const int _maxSignatureLength = 120;

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
                        description = "Path to the file (absolute or relative to project root)"
                    }
                },
                required = new[] { "file_path" }
            }));
    }

    public async Task<string> ExecuteAsync(string argumentsJson, ToolContext context)
    {
        var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        var filePath = args.GetProperty("file_path").GetString() ?? "";

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: file_path is required";
        }

        if (!Path.IsPathRooted(filePath))
        {
            filePath = Path.GetFullPath(Path.Combine(context.RootPath, filePath));
        }

        if (!File.Exists(filePath))
        {
            return $"Error: File not found: {filePath}";
        }

        var lines = await File.ReadAllLinesAsync(filePath);
        var relPath = Path.GetRelativePath(context.RootPath, filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        var symbols = ext switch
        {
            ".cs" => ParseCSharp(lines),
            ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => ParseTypeScript(lines),
            ".py" or ".pyw" => ParsePython(lines),
            ".java" or ".kt" or ".scala" => ParseJava(lines),
            ".go" => ParseGo(lines),
            _ => ParseGeneric(lines)
        };

        var sb = new StringBuilder();
        sb.AppendLine($"File: {relPath} ({lines.Length} lines)");

        if (symbols.Count == 0)
        {
            sb.AppendLine("\n(No structural elements detected)");
            return sb.ToString();
        }

        sb.AppendLine();

        foreach (var sym in symbols)
        {
            var indent = new string(' ', sym.Depth * 4);
            var lineNum = sym.Line.ToString().PadLeft(5);
            var sig = sym.Signature.Length > _maxSignatureLength
                ? sym.Signature[.._maxSignatureLength] + "..."
                : sym.Signature;
            sb.AppendLine($"{lineNum}: {indent}{sig}");
        }

        return sb.ToString();
    }

    // ── C# Parser ──────────────────────────────────────────────────

    private static readonly Regex _csNamespaceRegex = new(@"^\s*namespace\s+[\w.]+", RegexOptions.Compiled);

    private static readonly Regex _csTypeRegex = new(@"^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|partial|file|new|readonly|ref)\s+)*(class|interface|struct|enum|record)\b", RegexOptions.Compiled);

    private static readonly Regex _csMethodOrCtorRegex = new(@"^\s*(?:(?:public|private|protected|internal|static|abstract|async|override|virtual|new|extern|unsafe|sealed|partial)\s+)*[\w<>\[\]?,.\s]+\s+\w+\s*(<[^>]*>)?\s*\(", RegexOptions.Compiled);

    private static readonly Regex _csPropertyRegex = new(@"^\s*(?:(?:public|private|protected|internal|static|abstract|override|virtual|new|required|readonly)\s+)*[\w<>\[\]?,.\s]+\s+\w+\s*(=>|\{)", RegexOptions.Compiled);

    private static readonly Regex _csFieldRegex = new(@"^\s*(?:(?:public|private|protected|internal|static|readonly|volatile|const|new|required)\s+)+[\w<>\[\]?,.\s]+\s+\w+", RegexOptions.Compiled);

    private static readonly Regex _csEventRegex = new(@"^\s*(?:(?:public|private|protected|internal|static|new)\s+)*event\s+", RegexOptions.Compiled);

    private static readonly Regex _csDelegateRegex = new(@"^\s*(?:(?:public|private|protected|internal)\s+)*delegate\s+", RegexOptions.Compiled);

    private static List<OutlineSymbol> ParseCSharp(string[] lines)
    {
        var symbols = new List<OutlineSymbol>();
        int braceDepth = 0;
        bool inMultiLineComment = false;
        bool fileScopedNamespace = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Track multi-line comments
            if (inMultiLineComment)
            {
                if (trimmed.Contains("*/"))
                {
                    inMultiLineComment = false;
                }

                continue;
            }

            if (trimmed.StartsWith("/*"))
            {
                if (!trimmed.Contains("*/"))
                {
                    inMultiLineComment = true;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("//"))
            {
                continue;
            }

            if (trimmed.StartsWith("#"))
            {
                continue; // preprocessor
            }

            // Skip attributes (lines starting with [ and ending with ] before a declaration)
            if (trimmed.StartsWith('[') && (trimmed.EndsWith(']') || trimmed.EndsWith("],")))
            {
                continue;
            }

            int startDepth = braceDepth;

            // Count braces on this line (simplified — ignores braces in strings/chars)
            foreach (char c in line)
            {
                if (c == '{')
                {
                    braceDepth++;
                }
                else if (c == '}')
                {
                    braceDepth--;
                }
            }

            if (braceDepth < 0)
            {
                braceDepth = 0;
            }

            // Only care about structural levels (skip method bodies)
            // Depth 0 = top-level; depth 1 = inside a type; depth 2+ = inside methods
            int effectiveDepth = fileScopedNamespace ? startDepth + 1 : startDepth;

            // Skip method bodies (depth 2+ for traditional, depth 1+ adjusted for file-scoped)
            if (effectiveDepth > 1)
            {
                continue;
            }

            // Skip closing brace lines
            if (trimmed.StartsWith('}'))
            {
                continue;
            }

            // Skip 'using' directives
            if (trimmed.StartsWith("using ") && (trimmed.EndsWith(';') || trimmed.Contains('=')))
            {
                continue;
            }

            // ── Depth 0 (top-level) ──
            if (startDepth == 0)
            {
                if (_csNamespaceRegex.IsMatch(trimmed))
                {
                    var sig = CleanSignature(trimmed);
                    symbols.Add(new OutlineSymbol(i + 1, 0, sig));
                    if (trimmed.TrimEnd().EndsWith(';'))
                    {
                        fileScopedNamespace = true;
                    }

                    continue;
                }

                if (_csTypeRegex.IsMatch(trimmed))
                {
                    symbols.Add(new OutlineSymbol(i + 1, 0, CleanSignature(trimmed)));
                    continue;
                }

                // File-scoped namespace means members at depth 0 are really inside a namespace
                if (fileScopedNamespace && TryAddCsMember(trimmed, i + 1, 1, symbols))
                {
                    continue;
                }
            }

            // ── Depth 1 (inside a type) ──
            if (startDepth == 1 || (fileScopedNamespace && startDepth == 1))
            {
                int displayDepth = fileScopedNamespace ? 2 : 1;
                TryAddCsMember(trimmed, i + 1, displayDepth, symbols);
            }
        }

        return symbols;
    }

    private static bool TryAddCsMember(string trimmed, int lineNum, int depth, List<OutlineSymbol> symbols)
    {
        // Nested type
        if (_csTypeRegex.IsMatch(trimmed))
        {
            symbols.Add(new OutlineSymbol(lineNum, depth, CleanSignature(trimmed)));
            return true;
        }
        // Event
        if (_csEventRegex.IsMatch(trimmed))
        {
            symbols.Add(new OutlineSymbol(lineNum, depth, CleanSignature(trimmed)));
            return true;
        }
        // Delegate
        if (_csDelegateRegex.IsMatch(trimmed))
        {
            symbols.Add(new OutlineSymbol(lineNum, depth, CleanSignature(trimmed)));
            return true;
        }
        // Method or constructor (has parentheses)
        if (_csMethodOrCtorRegex.IsMatch(trimmed))
        {
            symbols.Add(new OutlineSymbol(lineNum, depth, CleanSignature(trimmed)));
            return true;
        }
        // Property (has => or { get/set)
        if (_csPropertyRegex.IsMatch(trimmed) && !trimmed.Contains('('))
        {
            symbols.Add(new OutlineSymbol(lineNum, depth, CleanSignature(trimmed)));
            return true;
        }
        // Field or constant
        if (_csFieldRegex.IsMatch(trimmed) && !trimmed.Contains('(') && !trimmed.Contains("=>"))
        {
            symbols.Add(new OutlineSymbol(lineNum, depth, CleanSignature(trimmed)));
            return true;
        }

        return false;
    }

    // ── TypeScript / JavaScript Parser ─────────────────────────────

    private static readonly Regex _tsClassRegex = new(@"^\s*(?:export\s+)?(?:abstract\s+)?(?:default\s+)?class\s+\w+", RegexOptions.Compiled);

    private static readonly Regex _tsInterfaceRegex = new(@"^\s*(?:export\s+)?(?:default\s+)?(?:interface|type|enum)\s+\w+", RegexOptions.Compiled);

    private static readonly Regex _tsFunctionRegex = new(@"^\s*(?:export\s+)?(?:async\s+)?(?:default\s+)?function\s+\w+", RegexOptions.Compiled);

    private static readonly Regex _tsArrowRegex = new(@"^\s*(?:export\s+)?(?:const|let|var)\s+\w+\s*=\s*(?:async\s+)?(?:\(|<)", RegexOptions.Compiled);

    private static readonly Regex _tsMethodRegex = new(@"^\s*(?:public|private|protected|static|async|abstract|readonly|get|set|override)\s+", RegexOptions.Compiled);

    private static List<OutlineSymbol> ParseTypeScript(string[] lines)
    {
        var symbols = new List<OutlineSymbol>();
        int braceDepth = 0;
        bool inMultiLineComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (inMultiLineComment)
            {
                if (trimmed.Contains("*/"))
                {
                    inMultiLineComment = false;
                }

                continue;
            }
            if (trimmed.StartsWith("/*"))
            {
                if (!trimmed.Contains("*/"))
                {
                    inMultiLineComment = true;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("//"))
            {
                continue;
            }

            if (trimmed.StartsWith('@') || trimmed.StartsWith("import ") || trimmed.StartsWith("from "))
            {
                continue;
            }

            int startDepth = braceDepth;
            foreach (char c in line)
            {
                if (c == '{')
                {
                    braceDepth++;
                }
                else if (c == '}')
                {
                    braceDepth--;
                }
            }

            if (braceDepth < 0)
            {
                braceDepth = 0;
            }

            if (startDepth > 1)
            {
                continue;
            }

            if (trimmed.StartsWith('}'))
            {
                continue;
            }

            if (startDepth == 0)
            {
                if (_tsClassRegex.IsMatch(trimmed)
                    || _tsInterfaceRegex.IsMatch(trimmed)
                    || _tsFunctionRegex.IsMatch(trimmed)
                    || _tsArrowRegex.IsMatch(trimmed))
                {
                    symbols.Add(new OutlineSymbol(i + 1, 0, CleanSignature(trimmed)));
                    continue;
                }
            }

            if (startDepth == 1)
            {
                // Class member: method, property, constructor
                if (_tsMethodRegex.IsMatch(trimmed)
                    || trimmed.StartsWith("constructor")
                    || Regex.IsMatch(trimmed, @"^\w+\s*[\(<]"))
                {
                    symbols.Add(new OutlineSymbol(i + 1, 1, CleanSignature(trimmed)));
                }
            }
        }

        return symbols;
    }

    // ── Python Parser ──────────────────────────────────────────────

    private static readonly Regex _pyClassRegex = new(@"^\s*class\s+\w+", RegexOptions.Compiled);
    private static readonly Regex _pyFunctionRegex = new(@"^\s*(?:async\s+)?def\s+\w+", RegexOptions.Compiled);

    private static List<OutlineSymbol> ParsePython(string[] lines)
    {
        var symbols = new List<OutlineSymbol>();
        bool inMultiLineString = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Rough multi-line string tracking
            var tripleCount = CountSubstring(line, "\"\"\"") + CountSubstring(line, "'''");
            if (inMultiLineString)
            {
                if (tripleCount % 2 == 1)
                {
                    inMultiLineString = false;
                }

                continue;
            }

            if (tripleCount % 2 == 1)
            {
                inMultiLineString = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith("import ") || trimmed.StartsWith("from "))
            {
                continue;
            }

            int indent = line.Length - line.TrimStart().Length;
            int depth = indent / 4; // Python convention: 4-space indent

            if (_pyClassRegex.IsMatch(trimmed))
            {
                symbols.Add(new OutlineSymbol(i + 1, depth, CleanSignature(trimmed)));
                continue;
            }
            if (_pyFunctionRegex.IsMatch(trimmed))
            {
                symbols.Add(new OutlineSymbol(i + 1, depth, CleanSignature(trimmed)));
            }
        }

        return symbols;
    }

    private static int CountSubstring(string source, string sub)
    {
        int count = 0, idx = 0;
        while ((idx = source.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += sub.Length;
        }
        return count;
    }

    // ── Java Parser ────────────────────────────────────────────────

    private static readonly Regex _javaTypeRegex = new(@"^\s*(?:(?:public|private|protected|static|abstract|final|sealed)\s+)*(?:class|interface|enum|record|@interface)\s+\w+", RegexOptions.Compiled);

    private static readonly Regex _javaMethodRegex = new(@"^\s*(?:(?:public|private|protected|static|abstract|final|synchronized|native|default|override)\s+)*[\w<>\[\]?,.\s]+\s+\w+\s*\(",
        RegexOptions.Compiled);

    private static List<OutlineSymbol> ParseJava(string[] lines)
    {
        var symbols = new List<OutlineSymbol>();
        int braceDepth = 0;
        bool inMultiLineComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (inMultiLineComment)
            {
                if (trimmed.Contains("*/"))
                {
                    inMultiLineComment = false;
                }

                continue;
            }

            if (trimmed.StartsWith("/*"))
            {
                if (!trimmed.Contains("*/"))
                {
                    inMultiLineComment = true;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("//"))
            {
                continue;
            }

            if (trimmed.StartsWith('@'))
            {
                continue; // annotations
            }

            if (trimmed.StartsWith("import ")
                || trimmed.StartsWith("package "))
            {
                continue;
            }

            int startDepth = braceDepth;
            foreach (char c in line)
            {
                if (c == '{')
                {
                    braceDepth++;
                }
                else if (c == '}')
                {
                    braceDepth--;
                }
            }

            if (braceDepth < 0)
            {
                braceDepth = 0;
            }

            if (startDepth > 1)
            {
                continue;
            }

            if (trimmed.StartsWith('}'))
            {
                continue;
            }

            if (startDepth == 0 && _javaTypeRegex.IsMatch(trimmed))
            {
                symbols.Add(new OutlineSymbol(i + 1, 0, CleanSignature(trimmed)));
                continue;
            }

            if (startDepth == 1)
            {
                if (_javaTypeRegex.IsMatch(trimmed))
                {
                    symbols.Add(new OutlineSymbol(i + 1, 1, CleanSignature(trimmed)));
                    continue;
                }
                if (_javaMethodRegex.IsMatch(trimmed))
                {
                    symbols.Add(new OutlineSymbol(i + 1, 1, CleanSignature(trimmed)));
                }
            }
        }

        return symbols;
    }

    // ── Go Parser ──────────────────────────────────────────────────

    private static readonly Regex _goFuncRegex = new(@"^\s*func\s+(?:\([^)]*\)\s+)?(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex _goTypeRegex = new(@"^\s*type\s+\w+\s+(struct|interface|int|string|float|bool|\[|map|func|chan)", RegexOptions.Compiled);
    private static readonly Regex _goTypeAliasRegex = new(@"^\s*type\s+\w+\s+=?\s*\w+", RegexOptions.Compiled);
    private static readonly Regex _goVarConstRegex = new(@"^\s*(?:var|const)\s+\w+", RegexOptions.Compiled);

    private static List<OutlineSymbol> ParseGo(string[] lines)
    {
        var symbols = new List<OutlineSymbol>();

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("//"))
            {
                continue;
            }

            if (trimmed.StartsWith("import ")
                || trimmed.StartsWith("package "))
            {
                continue;
            }

            if (_goFuncRegex.IsMatch(trimmed)
                || _goTypeRegex.IsMatch(trimmed)
                || _goTypeAliasRegex.IsMatch(trimmed)
                || _goVarConstRegex.IsMatch(trimmed))
            {
                symbols.Add(new OutlineSymbol(i + 1, 0, CleanSignature(trimmed)));
            }
        }

        return symbols;
    }

    // ── Generic Fallback Parser ────────────────────────────────────

    private static readonly Regex _genericPatterns = new(
        @"^\s*(?:(?:pub(?:lic)?|priv(?:ate)?|prot(?:ected)?|export|default|static|abstract|" +
        @"async|const|final|sealed|override|fn|func|fun|def|sub|function|class|struct|" +
        @"trait|impl|interface|enum|type|module|object|record|data)\s+)",
        RegexOptions.Compiled);

    private static List<OutlineSymbol> ParseGeneric(string[] lines)
    {
        var symbols = new List<OutlineSymbol>();
        int braceDepth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("//")
                || trimmed.StartsWith('#')
                || trimmed.StartsWith("--"))
            {
                continue;
            }

            int startDepth = braceDepth;
            foreach (char c in line)
            {
                if (c == '{')
                {
                    braceDepth++;
                }
                else if (c == '}')
                {
                    braceDepth--;
                }
            }

            if (braceDepth < 0)
            {
                braceDepth = 0;
            }

            if (startDepth > 1)
            {
                continue;
            }

            if (trimmed.StartsWith('}'))
            {
                continue;
            }

            if (_genericPatterns.IsMatch(trimmed))
            {
                symbols.Add(new OutlineSymbol(i + 1, startDepth, CleanSignature(trimmed)));
            }
        }

        return symbols;
    }

    // ── Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Clean up a signature line for display — remove trailing braces, semicolons, etc.
    /// </summary>
    private static string CleanSignature(string line)
    {
        var sig = line.TrimStart();
        // Remove trailing opening brace and whitespace
        sig = sig.TrimEnd();

        if (sig.EndsWith('{'))
        {
            sig = sig[..^1].TrimEnd();
        }
        // Remove trailing semicolons (for fields/consts)
        // Keep the semicolon for namespace declarations
        return sig;
    }
}

/// <summary>
/// Represents a structural element in a file outline
/// </summary>
public record OutlineSymbol(int Line, int Depth, string Signature);

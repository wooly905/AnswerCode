using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnswerCode.Services.Tools;

/// <summary>
/// Parses tool calls from LLM text output in the ReAct format.
/// The LLM is instructed to output tool calls as:
///   &lt;tool_call&gt;
///   {"name": "tool_name", "arguments": {"param1": "value1"}}
///   &lt;/tool_call&gt;
///
/// Also handles native model token formats (e.g. gpt-oss series) where tool calls
/// appear as raw tokens: &lt;|channel|&gt;...to=functions.name...&lt;|message|&gt;{...}&lt;|call|&gt;
///
/// This allows any text-generating LLM to act as an agent, without
/// requiring native tool calling (function calling) support.
/// </summary>
public static class ReActParser
{
    /// <summary>
    /// Regex to match &lt;tool_call&gt;...&lt;/tool_call&gt; blocks in LLM output.
    /// Supports optional whitespace/newlines inside the tags.
    /// </summary>
    private static readonly Regex _toolCallRegex = new(@"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Regex to match native model tool tokens (e.g. gpt-oss series):
    ///   &lt;|channel|&gt;...to=some.function_name...&lt;|message|&gt;{...}&lt;|call|&gt;
    /// Captures: (1) fully-qualified function name, (2) JSON arguments.
    /// </summary>
    private static readonly Regex _nativeToolCallRegex = new(
        @"<\|channel\|>[^<]*?to=(\S+?)\s[^<]*?<\|message\|>\s*(\{.*?\})\s*<\|call\|>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Regex to match any &lt;|...|&gt; special tokens for cleaning.
    /// </summary>
    private static readonly Regex _specialTokenRegex = new(
        @"<\|[^|]*?\|>",
        RegexOptions.Compiled);

    /// <summary>
    /// Maps model-internal function names to our registered tool names.
    /// Keys are the last segment after the dot (e.g. "glob_search" from "repo_browser.glob_search").
    /// </summary>
    private static readonly Dictionary<string, string> _nativeToolNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search"] = "grep_search",
        ["search_file"] = "grep_search",
        ["glob_search"] = "glob_search",
        ["open_file"] = "read_file",
        ["read_file"] = "read_file",
        ["print_tree"] = "list_directory",
        ["list_directory"] = "list_directory",
        ["get_file_outline"] = "get_file_outline",
        ["find_definition"] = "find_definition",
        ["find_references"] = "find_references",
        ["get_related_files"] = "get_related_files",
        ["read_symbol"] = "read_symbol",
        ["find_tests"] = "find_tests",
        ["call_graph"] = "call_graph",
        ["web_search"] = "web_search",
        ["config_lookup"] = "config_lookup",
        ["repo_map"] = "repo_map",
    };

    /// <summary>
    /// Maps model-internal argument keys to our tool argument keys.
    /// </summary>
    private static readonly Dictionary<string, string> _argKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["query"] = "pattern",
        ["path"] = "file_path",
        ["depth"] = "max_depth",
        ["line_start"] = "offset",
        ["max_results"] = "",  // drop — not used by our tools
    };

    /// <summary>
    /// Parse all tool calls from an LLM text response.
    /// Tries the standard &lt;tool_call&gt; format first, then falls back to native model tokens.
    /// Returns an empty list if no tool calls are found (meaning the LLM is giving a final answer).
    /// </summary>
    public static List<ReActToolCall> ParseToolCalls(string llmResponse)
    {
        var results = ParseStandardToolCalls(llmResponse);
        if (results.Count > 0)
            return results;

        return ParseNativeToolCalls(llmResponse);
    }

    /// <summary>
    /// Parse standard &lt;tool_call&gt; format.
    /// </summary>
    private static List<ReActToolCall> ParseStandardToolCalls(string llmResponse)
    {
        var results = new List<ReActToolCall>();
        var matches = _toolCallRegex.Matches(llmResponse);

        foreach (Match match in matches)
        {
            try
            {
                var json = match.Groups[1].Value;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var name = root.GetProperty("name").GetString() ?? "";
                var arguments = root.TryGetProperty("arguments", out var args)
                    ? args.GetRawText()
                    : "{}";

                if (!string.IsNullOrWhiteSpace(name))
                {
                    results.Add(new ReActToolCall
                    {
                        FunctionName = name,
                        Arguments = arguments
                    });
                }
            }
            catch (JsonException)
            {
                // Skip malformed tool calls — the LLM may have produced bad JSON
            }
        }

        return results;
    }

    /// <summary>
    /// Parse native model tool tokens (e.g. gpt-oss series):
    ///   &lt;|channel|&gt;...to=repo_browser.glob_search...&lt;|message|&gt;{...}&lt;|call|&gt;
    /// Maps function names and argument keys to our tool definitions.
    /// </summary>
    private static List<ReActToolCall> ParseNativeToolCalls(string llmResponse)
    {
        var results = new List<ReActToolCall>();
        var matches = _nativeToolCallRegex.Matches(llmResponse);

        foreach (Match match in matches)
        {
            try
            {
                var rawFunctionName = match.Groups[1].Value;
                var rawArgs = match.Groups[2].Value;

                // Extract last segment: "repo_browser.glob_search" → "glob_search"
                var shortName = rawFunctionName.Contains('.')
                    ? rawFunctionName[(rawFunctionName.LastIndexOf('.') + 1)..]
                    : rawFunctionName;

                // Map to our tool name
                if (!_nativeToolNameMap.TryGetValue(shortName, out var toolName))
                    toolName = shortName; // pass through if no mapping — tool registry will report "unknown tool"

                // Remap argument keys
                var mappedArgs = RemapArgumentKeys(rawArgs);

                results.Add(new ReActToolCall
                {
                    FunctionName = toolName,
                    Arguments = mappedArgs
                });
            }
            catch (JsonException)
            {
                // Skip malformed tool calls
            }
        }

        return results;
    }

    /// <summary>
    /// Remap argument keys from the model's native schema to our tool schema.
    /// E.g. "query" → "pattern", "path" → "file_path", "depth" → "max_depth".
    /// </summary>
    private static string RemapArgumentKeys(string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return argsJson;

            var mapped = new Dictionary<string, JsonElement>();
            foreach (var prop in root.EnumerateObject())
            {
                if (_argKeyMap.TryGetValue(prop.Name, out var newKey))
                {
                    if (!string.IsNullOrEmpty(newKey)) // empty means drop
                        mapped[newKey] = prop.Value;
                }
                else
                {
                    mapped[prop.Name] = prop.Value; // keep as-is
                }
            }

            return JsonSerializer.Serialize(mapped);
        }
        catch
        {
            return argsJson;
        }
    }

    /// <summary>
    /// Check whether the LLM response contains any tool call tags (standard or native).
    /// </summary>
    public static bool HasToolCalls(string llmResponse)
    {
        return _toolCallRegex.IsMatch(llmResponse) || _nativeToolCallRegex.IsMatch(llmResponse);
    }

    /// <summary>
    /// Extract the "thinking" text that the LLM output before/after tool calls.
    /// Strips both standard &lt;tool_call&gt; blocks and native model tokens.
    /// </summary>
    public static string ExtractThinkingText(string llmResponse)
    {
        var cleaned = _toolCallRegex.Replace(llmResponse, "");
        cleaned = CleanNativeTokens(cleaned);
        return cleaned.Trim();
    }

    /// <summary>
    /// Remove native model special tokens (&lt;|start|&gt;, &lt;|channel|&gt;, etc.)
    /// and any surrounding tool call text from a response string.
    /// </summary>
    public static string CleanNativeTokens(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("<|"))
            return text;

        // Strip complete native tool call blocks first
        text = _nativeToolCallRegex.Replace(text, "");

        // Strip any remaining <|...|> tokens
        text = _specialTokenRegex.Replace(text, "");

        // Clean up leftover fragments: "assistant", "commentary", "analysis", "to=..." etc.
        text = Regex.Replace(text, @"\bassistant\b", "", RegexOptions.IgnoreCase);

        return text.Trim();
    }

    /// <summary>
    /// Format tool execution results as a user message for the next conversation turn.
    /// </summary>
    public static string FormatToolResults(List<(string ToolName, string Result)> toolResults)
    {
        if (toolResults.Count == 1)
        {
            var (name, result) = toolResults[0];
            return $"<tool_result name=\"{name}\">\n{result}\n</tool_result>";
        }

        var parts = toolResults.Select(tr => $"<tool_result name=\"{tr.ToolName}\">\n{tr.Result}\n</tool_result>");

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Build the ReAct system prompt with tool descriptions embedded.
    /// </summary>
    public static string BuildReActSystemPrompt(string toolDescriptions)
    {
        return $@"You are an expert code analyst with access to tools for exploring a codebase.
Your task is to answer user questions about the code by using the available tools.

## Available Tools

{toolDescriptions}

## How to Use Tools

When you need to use a tool, output the following XML tag:

<tool_call>
{{""name"": ""tool_name"", ""arguments"": {{""param1"": ""value1""}}}}
</tool_call>

IMPORTANT RULES:
- You can output one or more <tool_call> tags per response.
- After outputting tool call(s), STOP and wait for the results.
- The tool results will be provided to you in <tool_result> tags.
- After receiving results, you can use more tools or provide your final answer.
- When you have enough information to answer, respond directly WITHOUT any <tool_call> tags.

## Strategy
A project overview (directory structure and metadata) is automatically provided with the user's question.
1. Review the project overview already provided — it includes directory structure and key metadata.
2. Use `get_file_outline` to understand a file's structure before reading it (much more efficient than reading the whole file).
3. Use `grep_search` to find relevant code by searching for keywords, class names, function names, or patterns.
4. Use `find_definition` to locate where a class, method, interface, or type is defined.
5. Use `get_related_files` to discover a file's dependencies and dependents.
6. Use `glob_search` to find files by name pattern (faster when you know the filename pattern).
7. Use `read_file` to read specific sections of files — use offset and max_lines for efficiency.
8. Use `list_directory` only if you need to explore a subdirectory not shown in the overview.
9. If your initial search doesn't find what you need, try different keywords, patterns, or file filters.

## Rules
- Be thorough: search with multiple keywords and patterns if the first search doesn't fully answer the question.
- Be precise: cite specific file paths and line numbers when relevant.
- Use markdown formatting for code snippets and file references.
- If you cannot find the answer after thorough searching, say so honestly rather than guessing.
- Respond in the same language as the user's question.
- Focus on the code that exists — don't make assumptions about code you haven't read.
- When analyzing code flow, trace through function calls and class relationships.
- Prefer get_file_outline over read_file when you only need to understand a file's structure.
- Prefer find_definition over grep_search when looking for where something is defined.
";
    }
}

/// <summary>
/// Represents a tool call parsed from LLM text output.
/// </summary>
public class ReActToolCall
{
    /// <summary>
    /// The tool/function name to call
    /// </summary>
    public required string FunctionName
    {
        get; init;
    }

    /// <summary>
    /// The arguments as a JSON string
    /// </summary>
    public required string Arguments
    {
        get; init;
    }
}

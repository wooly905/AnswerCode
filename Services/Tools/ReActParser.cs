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
    /// Parse all tool calls from an LLM text response.
    /// Returns an empty list if no tool calls are found (meaning the LLM is giving a final answer).
    /// </summary>
    public static List<ReActToolCall> ParseToolCalls(string llmResponse)
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
    /// Check whether the LLM response contains any tool call tags.
    /// </summary>
    public static bool HasToolCalls(string llmResponse)
    {
        return _toolCallRegex.IsMatch(llmResponse);
    }

    /// <summary>
    /// Extract the "thinking" text that the LLM output before/after tool calls.
    /// This is useful for streaming progress to the user.
    /// </summary>
    public static string ExtractThinkingText(string llmResponse)
    {
        // Remove all <tool_call>...</tool_call> blocks
        return _toolCallRegex.Replace(llmResponse, "").Trim();
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

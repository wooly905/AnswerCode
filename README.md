# AnswerCode

AI-powered code Q&A system. Ask questions about your codebase and get intelligent answers using large language models (LLMs) with an agentic tool-calling loop.

## Features

- **Agentic Q&A**: An AI agent uses tools (grep, read file, list directory, glob search, file outline, find definition, related files) to explore your codebase and answer questions autonomously
- **Dual Answer Modes**: Choose between **Developer** mode (technical, with file paths and line numbers) and **PM** mode (plain language, business-focused, no code snippets) for each question
- **Multiple LLM Providers**: Dynamically configurable—add any number of OpenAI-compatible or Azure OpenAI providers via `appsettings.json`
- **ReAct Fallback Loop**: Providers that do not support native function calling automatically fall back to a text-based ReAct loop using `<tool_call>` XML tags, so any LLM can act as an agent
- **Streaming Progress**: Real-time SSE streaming shows each tool call as it happens, including a result summary, expandable detail items, and duration
- **Token Usage Tracking**: Input and output token counts are tracked across all LLM calls in an agent run and surfaced in the API response and UI
- **Multi-Language Project Support**: Auto-detects and summarizes project metadata for .NET, Node.js, Python, Go, Rust, and Java projects
- **Dark Theme UI**: Web interface with syntax highlighting, Markdown rendering, and Mermaid diagram support
- **Structured Logging**: Request/response logging via Serilog with console and rolling file sinks

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- LLM API access (OpenAI or Azure OpenAI)

## Quick Start

1. Clone the repository and navigate to the project folder:
   ```bash
   cd AnswerCode
   ```

2. Configure LLM providers in `appsettings.json` (see [Configuration](#configuration) below).

3. Run the application:
   ```bash
   dotnet run
   ```

4. Open a browser to **http://localhost:5000** (or https://localhost:5001).

5. The default project path is loaded from configuration (see [Configuration](#configuration)). You can override it in the UI if needed. Select a model provider, enter your question, and click **Answer as Developer** or **Answer as PM**.

## Answer Modes

Two distinct modes tailor the agent's behavior and response style:

| Mode | Button | Audience | Style |
|------|--------|----------|-------|
| **Developer** | Answer as Developer | Engineers | Technical; cites file paths, line numbers, class/method names, and code snippets |
| **PM** | Answer as PM | Program/Project Managers | Plain language; describes business workflows and module interactions without raw code |

The `UserRole` field in the API request (`"Developer"` or `"PM"`) selects the mode. The default (omitted) is Developer.

## Configuration

All settings are configured in `appsettings.json` (or `appsettings.Local.json` for local overrides).

### Project Settings

The default project path for code exploration is configured under the `QASourceCodePath` section:

```json
{
  "QASourceCodePath": {
    "DefaultPath": "project-code"
  }
}
```

- **`DefaultPath`**: The default directory to explore. Can be a relative path (resolved from the application base directory) or an absolute path (e.g. `C:\\repos\\my-project`).
- The web UI automatically loads this value on startup. Users can still override it in the input field if needed.

### LLM Providers

LLM providers are configured under the `LLM` section. You can add as many providers as needed; each one appears in the UI's provider dropdown.

```json
{
  "LLM": {
    "DefaultProvider": "OpenAI",
    "Providers": {
      "OpenAI": {
        "Endpoint": "https://your-endpoint.openai.com",
        "ApiKey": "your-api-key",
        "Model": "gpt-4o",
        "DisplayName": "GPT-4o"
      },
      "AzureOpenAI": {
        "Endpoint": "https://your-resource.cognitiveservices.azure.com/",
        "ApiKey": "your-api-key",
        "DeploymentName": "gpt-4o",
        "DisplayName": "Azure GPT-4o"
      }
    }
  }
}
```

### Provider Types

- **AzureOpenAI**: Use `Endpoint`, `ApiKey`, `DeploymentName`, and optionally `DisplayName`.
- **OpenAI / OpenAI-compatible** (any other key): Use `Endpoint`, `ApiKey`, `Model`, and optionally `DisplayName`. The factory treats every non-`AzureOpenAI` key as an OpenAI-compatible provider.

### Local Overrides

Use `appsettings.Local.json` for local secrets and overrides. This file is gitignored and will override values from `appsettings.json` when present. Copy the structure from `appsettings.json` and fill in your API keys and project path.

## Agent Tools

The agent uses these tools to explore your codebase:

| Tool | Description |
|------|-------------|
| `get_file_outline` | Get structural outline of a file (classes, methods, properties) with line numbers — much more token-efficient than reading the whole file |
| `find_definition` | Find where a symbol (class, interface, method, etc.) is defined — more precise than grep |
| `get_related_files` | Find a file's dependencies (imports) and dependents (files that reference it) |
| `grep_search` | Search file contents by pattern (regex) |
| `glob_search` | Find files by name pattern (e.g. `*.cs`) |
| `read_file` | Read file contents (with optional line range) |
| `list_directory` | List files in a subdirectory (project root structure is auto-injected) |

**Auto-injected context:** The agent automatically receives a project overview (directory structure, language, framework, dependencies) at the start of each conversation, eliminating the need for an initial `list_directory` call and saving one full LLM round-trip.

**Multi-language project detection:** The overview builder auto-detects project metadata from `.csproj` (.NET), `package.json` (Node.js), `requirements.txt` / `pyproject.toml` (Python), `go.mod` (Go), `Cargo.toml` (Rust), and `pom.xml` / `build.gradle` (Java).

## ReAct Fallback Loop

When a configured provider reports `SupportsToolCalling = false`, the agent automatically switches to a **ReAct text loop** instead of native function calling. In this mode:

- The LLM is given embedded tool descriptions in its system prompt.
- Tool calls are expressed as `<tool_call>{"name": "...", "arguments": {...}}</tool_call>` XML tags in plain text output.
- The server parses these tags (via `ReActParser`), executes the tools, and returns results in `<tool_result>` tags for the next turn.
- Progress events and token tracking work the same as with native tool calling.

This allows any text-generating LLM to act as an agent without requiring OpenAI-style function calling support.

## API Reference

### POST `/api/codeqa/ask`
Synchronous Q&A — runs the agent and returns the full answer in one response.

**Request body:**
```json
{
  "question": "How does authentication work?",
  "projectPath": "C:\\repos\\my-project",
  "modelProvider": "OpenAI",
  "userRole": "Developer",
  "sessionId": "optional-uuid"
}
```

**Response** (`AnswerResponse`):
```json
{
  "answer": "...",
  "relevantFiles": ["Services/AuthService.cs"],
  "processingTimeMs": 4200,
  "sessionId": "...",
  "toolCallCount": 5,
  "iterationCount": 3,
  "toolCalls": [...],
  "totalInputTokens": 12000,
  "totalOutputTokens": 800
}
```

### POST `/api/codeqa/ask/stream`
Streaming Q&A — returns Server-Sent Events (SSE). Each event is a JSON-encoded `AgentEvent`.

**Event types:**

| Type | When emitted |
|------|--------------|
| `Started` | Agent begins processing |
| `ToolCallStart` | A tool call is about to execute (includes `toolName`, `toolArgs`, `summary`) |
| `ToolCallEnd` | A tool call completed (adds `resultSummary`, `resultDetails`, `detailItems`, `durationMs`) |
| `Answer` | Final answer is ready (`result` contains the full `AnswerResponse`) |
| `Error` | An error occurred |

### GET `/api/codeqa/providers`
Returns the display names of all configured and successfully initialized LLM providers.

### GET `/api/codeqa/settings/defaultPath`
Returns the default project path from configuration.

### GET `/api/codeqa/structure?projectPath=...`
Returns the directory tree of the specified project path (up to 4 levels deep).

### GET `/api/codeqa/file?filePath=...&maxLines=...&offset=...`
Returns the contents of a specific file with optional pagination.

## Project Structure

```
AnswerCode/
├── Controllers/       # API controllers (CodeQAController)
├── Models/            # DTOs and configuration models
├── Services/
│   ├── Providers/     # LLM providers (OpenAIProvider, AzureOpenAIProvider)
│   └── Tools/         # Agent tools + ReActParser
├── wwwroot/           # Static frontend (index.html)
├── appsettings.json   # Main configuration
└── appsettings.Local.json  # Local overrides (gitignored)
```

## License

See repository for license details.

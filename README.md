# AnswerCode

> 🌐 **English** | [繁體中文](README.zh-TW.md)

AI-powered code Q&A system. Ask questions about your codebase and get intelligent answers using large language models (LLMs) with an agentic tool-calling loop.

## Features

- **Source Code Upload**: Upload your project files directly in the browser (drag & drop files or folders, up to 20 MB) — no server-side path configuration required
- **Agentic Q&A**: An AI agent uses tools (grep, read file, list directory, glob search, file outline, find definition, related files) to explore your codebase and answer questions autonomously
- **Dual Answer Modes**: Choose between **Developer** mode (technical, with file paths and line numbers) and **PM** mode (plain language, business-focused, no code snippets) for each question
- **Multiple LLM Providers**: Dynamically configurable — add any number of OpenAI-compatible, Azure OpenAI, or Ollama providers via `appsettings.json`
- **ReAct Fallback Loop**: Providers that do not support native function calling automatically fall back to a text-based ReAct loop using `<tool_call>` XML tags, so any LLM can act as an agent
- **Streaming Progress**: Real-time SSE streaming shows each tool call as it happens, including a result summary, expandable detail items, and duration
- **Token Usage Tracking**: Input and output token counts are tracked across all LLM calls in an agent run and surfaced in the API response and UI
- **Multi-Language Project Support**: Auto-detects and summarizes project metadata for .NET, Node.js, Python, Go, Rust, Java, and C/C++ projects
- **Dark Theme UI**: Web interface with syntax highlighting, Markdown rendering, Mermaid diagram support with interactive zoom/pan and fullscreen view
- **Structured Logging**: Request/response logging via Serilog with console and rolling file sinks

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- LLM API access (OpenAI, Azure OpenAI, or Ollama)

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

5. **Upload your source code** using the drag-and-drop area or the Browse Files / Browse Folder buttons. Select a model provider, enter your question, and click **Answer as Developer** or **Answer as PM**.

## Answer Modes

Two distinct modes tailor the agent's behavior and response style:

| Mode | Button | Audience | Style |
|------|--------|----------|-------|
| **Developer** | Answer as Developer | Engineers | Technical; cites file paths, line numbers, class/method names, and code snippets |
| **PM** | Answer as PM | Program/Project Managers | Plain language; describes business workflows and module interactions without raw code |

The `UserRole` field in the API request (`"Developer"` or `"PM"`) selects the mode. The default (omitted) is Developer.

## Source Code Upload

Source code is uploaded directly from the browser:

- Click **Browse Files** to select individual files, or **Browse Folder** to select an entire folder (preserving relative paths).
- Drag and drop files or folders onto the upload area.
- The total upload size limit is **20 MB**.
- Once uploaded, a green status badge shows the folder ID and file count. Click **Remove** to delete the uploaded code from the server.
- Multiple uploads are supported — each upload gets a unique folder ID on the server under `wwwroot/source-code/{folderId}/`.

The uploaded folder ID is automatically used as the `projectPath` for all Q&A requests.

## Configuration

All settings are configured in `appsettings.json` (or `appsettings.Local.json` for local overrides).

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
        "Model": "gpt-4o",
        "DisplayName": "Azure GPT-4o"
      },
      "Ollama": {
        "Endpoint": "http://localhost:11434/v1/",
        "ApiKey": "ollama",
        "Model": "llama3",
        "DisplayName": "Ollama Llama3"
      }
    }
  }
}
```

### Provider Types

- **AzureOpenAI**: Use `Endpoint`, `ApiKey`, `Model`, and optionally `DisplayName`. The key must contain `azure` (case-insensitive).
- **OpenAI / OpenAI-compatible** (any other key, including Ollama): Use `Endpoint`, `ApiKey`, `Model`, and optionally `DisplayName`. The factory treats every non-AzureOpenAI key as an OpenAI-compatible provider — Ollama works out of the box via its `/v1/` endpoint.

### Local Overrides

Use `appsettings.Local.json` for local secrets and overrides. This file is gitignored and will override values from `appsettings.json` when present. Copy the structure from `appsettings.json` and fill in your API keys.

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

**Multi-language project detection:** The overview builder auto-detects project metadata from `.csproj` (.NET), `package.json` (Node.js), `requirements.txt` / `pyproject.toml` (Python), `go.mod` (Go), `Cargo.toml` (Rust), `pom.xml` / `build.gradle` (Java), and `CMakeLists.txt` / `Makefile` (C/C++).

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
  "projectPath": "abd2b91fdb12",
  "modelProvider": "OpenAI",
  "userRole": "Developer",
  "sessionId": "optional-uuid"
}
```

> **`projectPath`** can be a `folderId` returned by the upload endpoint (e.g. `"abd2b91fdb12"`) or an absolute path on the server.

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

### POST `/api/codeqa/upload`
Upload source code files. Accepts multipart/form-data with `files[]` and optional `relativePaths[]` fields.

**Response:**
```json
{
  "folderId": "abd2b91fdb12",
  "fileCount": 42,
  "totalSizeBytes": 186366,
  "totalSizeMB": 0.18
}
```

### DELETE `/api/codeqa/upload/{folderId}`
Delete a previously uploaded source-code folder.

### GET `/api/codeqa/uploads`
List all existing uploaded source-code folders (folderId, createdUtc, fileCount).

### GET `/api/codeqa/providers`
Returns the display names of all configured and successfully initialized LLM providers.

### GET `/api/codeqa/structure?projectPath=...`
Returns the directory tree of the specified project path (up to 4 levels deep).

### GET `/api/codeqa/file?filePath=...&maxLines=...&offset=...`
Returns the contents of a specific file with optional pagination.

## Project Structure

```
AnswerCode/
├── Controllers/           # API controller (CodeQAController)
├── Models/                # DTOs and configuration models
├── Services/
│   ├── Providers/         # LLM provider implementations (OpenAI, AzureOpenAI)
│   └── Tools/             # Agent tools + ReActParser
├── wwwroot/               # Static frontend (index.html)
│   └── source-code/       # Uploaded source code folders (runtime, gitignored)
├── appsettings.json       # Main configuration
└── appsettings.Local.json # Local overrides (gitignored)
```

## License

See repository for license details.

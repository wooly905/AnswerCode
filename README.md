# AnswerCode

> 🌐 **English** | [繁體中文](README.zh-TW.md)

AI-powered code Q&A system. Ask questions about your codebase and get intelligent answers using large language models (LLMs) with an agentic tool-calling loop.

## Features

- **Source Code Upload**: Upload your project files directly in the browser (drag & drop files or folders) — no server-side path configuration required
- **Google Login & Persistent Storage**: Sign in with Google to get dedicated persistent storage (default 300 MB quota) — uploaded projects survive across browser sessions and can be managed from the Dashboard
- **User Dashboard**: Authenticated users get a `/dashboard` page showing all uploaded projects, storage usage with a visual progress bar, and the ability to delete individual projects
- **Agentic Q&A**: An AI agent uses tools (grep, read file, read symbol, list directory, glob search, file outline, find definition, find references, find tests, related files, repo map, call graph, web search, config lookup) to explore your codebase and answer questions autonomously
- **Dual Answer Modes**: Choose between **Developer** mode (technical, with file paths and line numbers) and **PM** mode (plain language, business-focused, no code snippets) for each question
- **Multiple LLM Providers**: Dynamically configurable — add any number of OpenAI-compatible, Azure OpenAI, or Ollama providers via `appsettings.json`
- **ReAct Fallback Loop**: Providers that do not support native function calling automatically fall back to a text-based ReAct loop using `<tool_call>` XML tags, so any LLM can act as an agent
- **SubAgent Architecture**: Follow-up questions use a 3-phase SubAgent design — (1) resolve the follow-up into a standalone question using conversation history, (2) run the agentic tool loop without history to save tokens, (3) synthesize the final answer with history context. This reduces token consumption and supports up to **25 Q&A rounds** per session
- **Conversation History Inspector**: Click the **Main** token counter in the top bar to view the exact conversation turns the LLM remembers, with a download button to export the history as Markdown
- **Streaming Progress**: Real-time SSE streaming shows each tool call as it happens, including a result summary, expandable detail items, and duration
- **Token Usage Tracking**: Main agent (context resolution + synthesis) and SubAgent (tool loop) token counts are tracked separately and displayed in the top bar as **Main / Sub / Total**
- **Multi-Language Project Support**: Auto-detects and summarizes project metadata for .NET, Node.js, Python, Go, Rust, Java, and C/C++ projects
- **Hybrid Multi-Language Code Analysis**: `C#` uses Roslyn for precise symbol reads and reference lookup, while JavaScript, TypeScript, Python, Java, Go, Rust, and C/C++ use heuristic symbol, reference, and test discovery
- **Dark Theme UI**: Web interface with syntax highlighting, Markdown rendering, Mermaid diagram support with interactive zoom/pan and fullscreen view
- **Automatic Upload Cleanup**: Anonymous uploads are automatically deleted when the user leaves the page (`beforeunload` + `sendBeacon`), with a background service as a safety net that removes expired uploads based on a configurable TTL
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

4. Open a browser to **http://localhost:5000**.

5. **Upload your source code** using the drag-and-drop area or the Browse Files / Browse Folder buttons. Select a model provider, enter your question, and click **Answer as Developer** or **Answer as PM**.

## Answer Modes

Two distinct modes tailor the agent's behavior and response style:

| Mode | Button | Audience | Style |
|------|--------|----------|-------|
| **Developer** | Answer as Developer | Engineers | Technical; cites file paths, line numbers, class/method names, and code snippets |
| **PM** | Answer as PM | Program/Project Managers | Plain language; describes business workflows and module interactions without raw code |

The mode is selected directly from the UI using **Answer as Developer** or **Answer as PM**.

## Source Code Upload

Source code is uploaded directly from the browser:

- Click **Browse Files** to select individual files, or **Browse Folder** to select an entire folder (preserving relative paths).
- Drag and drop files or folders onto the upload area.
- Multiple uploads are supported — each upload gets a unique folder ID.
- Once uploaded, a green status badge shows the folder ID and file count. Click **Remove** to delete the uploaded code from the server.

**Anonymous users** (not signed in):
- Upload size limit: **20 MB** per upload.
- Files are stored under `wwwroot/source-code/{folderId}/` and automatically deleted when the browser tab is closed (`navigator.sendBeacon()`). A background service acts as a safety net, removing expired uploads after the configured TTL (default: 120 minutes).

**Authenticated users** (signed in with Google):
- Upload size limit: **300 MB** per upload, with a total storage quota (default 300 MB, configurable).
- Files are stored under the user's dedicated directory and persist across sessions.
- Manage all uploaded projects from the **Dashboard** (`/dashboard`).

The uploaded folder ID is automatically used as the `projectPath` for all Q&A requests.

## Authentication & Dashboard

AnswerCode supports optional Google OAuth login. Authentication is **not required** to use the Q&A feature — anonymous users can upload code and ask questions as before.

Signing in unlocks:
- **Persistent storage** — uploaded projects are saved to your account and available across browser sessions.
- **Higher upload limit** — 300 MB per upload (vs. 20 MB anonymous).
- **Dashboard** — visit `/dashboard` to see all your uploaded projects, monitor storage usage, and delete projects you no longer need.

A **dev-login** shortcut (`/api/auth/dev-login`) is available in Development mode for local testing without Google OAuth credentials.

## Configuration

All settings are configured in `appsettings.json`.

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

### Google Authentication

Google OAuth is configured under the `Authentication` section. Obtain a Client ID and Client Secret from the [Google Cloud Console](https://console.cloud.google.com/apis/credentials).

```json
{
  "Authentication": {
    "Google": {
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret"
    }
  }
}
```

Authentication is optional — the app works fully for anonymous users without these credentials.

### User Storage Quota

The per-user storage limit for authenticated users is configured under `UserStorage`:

```json
{
  "UserStorage": {
    "MaxSizeMB": 300
  }
}
```

- `MaxSizeMB`: Maximum total storage per user in megabytes (default: 300).

### Web Search (Tavily)

The `web_search` tool uses the [Tavily Search API](https://tavily.com/) to let the agent retrieve external information. Configure the API key under the `Tavily` section:

```json
{
  "Tavily": {
    "ApiKey": "tvly-your-api-key"
  }
}
```

If no API key is configured, the tool will return an error message and the agent will skip web search.

### Upload Cleanup

Automatic cleanup of expired anonymous uploads is configured under the `UploadCleanup` section:

```json
{
  "UploadCleanup": {
    "ScanIntervalMinutes": 10,
    "MaxAgeMinutes": 120
  }
}
```

- `ScanIntervalMinutes`: How often the background service scans for expired folders (default: 10).
- `MaxAgeMinutes`: Folders with no file activity beyond this age are deleted (default: 120).

## Agent Tools

The agent uses these tools to explore your codebase:

| Tool | Description |
|------|-------------|
| `get_file_outline` | Get structural outline of a file (classes, methods, properties) with line numbers — much more token-efficient than reading the whole file |
| `find_definition` | Find where a symbol (class, interface, method, etc.) is defined — more precise than grep |
| `find_references` | Find where a symbol is used, called, inherited, implemented, or imported across the repository |
| `find_tests` | Find likely tests related to a source symbol or file |
| `get_related_files` | Find a file's dependencies (imports) and dependents (files that reference it) |
| `repo_map` | Generate a repository map showing module boundaries, architectural roles, cross-module dependencies, entry points, and a Mermaid diagram |
| `call_graph` | Generate a static call graph from a method/function — trace downstream calls or upstream callers with cycle detection and confidence labels |
| `grep_search` | Search file contents by pattern (regex) |
| `glob_search` | Find files by name pattern (e.g. `*.cs`) |
| `read_file` | Read file contents (with optional line range) |
| `read_symbol` | Read one exact symbol definition with optional body/comments instead of reading a whole file |
| `list_directory` | List files in a subdirectory (project root structure is auto-injected) |
| `web_search` | Search the web via Tavily Search API for external information — library docs, API references, best practices, error explanations, or latest updates |
| `config_lookup` | Look up a configuration key across all config files in the project — finds where a key is defined, its value in each source, and which value wins by precedence. Supports C#, JavaScript, TypeScript, Python, Java, Go, Rust, and C/C++ config patterns |

**Auto-injected context:** The agent automatically receives a project overview (directory structure, language, framework, dependencies) at the start of each conversation, eliminating the need for an initial `list_directory` call and saving one full LLM round-trip.

**Multi-language project detection:** The overview builder auto-detects project metadata from `.csproj` (.NET), `package.json` (Node.js), `requirements.txt` / `pyproject.toml` (Python), `go.mod` (Go), `Cargo.toml` (Rust), `pom.xml` / `build.gradle` (Java), and `CMakeLists.txt` / `Makefile` (C/C++).

**Symbol-aware analysis:**

- `C#` paths use Roslyn-backed analysis for `read_symbol`, `find_references`, `find_tests`, and `call_graph`.
- JavaScript, TypeScript, Python, Java, Go, Rust, and C/C++ use heuristic parsing and matching for those same tools.

## ReAct Fallback Loop

When a configured provider reports `SupportsToolCalling = false`, the agent automatically switches to a **ReAct text loop** instead of native function calling. In this mode:

- The LLM is given embedded tool descriptions in its system prompt.
- Tool calls are expressed as `<tool_call>{"name": "...", "arguments": {...}}</tool_call>` XML tags in plain text output.
- The server parses these tags (via `ReActParser`), executes the tools, and returns results in `<tool_result>` tags for the next turn.
- Progress events and token tracking work the same as with native tool calling.

This allows any text-generating LLM to act as an agent without requiring OpenAI-style function calling support.

## SubAgent Architecture

When the user asks a follow-up question (i.e., conversation history exists), the system splits the work into three phases to reduce token consumption:

| Phase | Role | History Included | LLM Calls |
|-------|------|-----------------|-----------|
| **1. Context Resolution** | Resolve the follow-up into a self-contained question | Yes | 1 |
| **2. SubAgent Tool Loop** | Run the full agentic research loop | **No** | 5–50 |
| **3. Answer Synthesis** | Combine research findings with conversation context | Yes | 1 |

The first question in a session (no history) skips directly to the tool loop with zero overhead.

**Why it matters:** In the previous design, conversation history was sent with every LLM call in the tool loop (5–50 calls). With SubAgent, history is only sent twice (Phase 1 + Phase 3), making the token cost nearly independent of history length. This allows supporting **25 Q&A rounds** (50 turns) per session without context overflow.

The top bar shows **Main** (Phase 1 + 3) and **Sub** (Phase 2) token usage separately. Clicking **Main** opens a modal showing the exact conversation turns in the LLM's memory, with a button to download the history as Markdown.

## User Experience Notes

- Uploading code creates an isolated workspace under `wwwroot/source-code/{folderId}/`.
- The selected upload is automatically reused for follow-up questions in the UI.
- Long-running answers stream progress live, including tool activity, summaries, and timing.
- The final answer highlights relevant files and overall tool usage so users can inspect how the agent reached its conclusion.

## Project Structure

```
AnswerCode/
├── Controllers/
│   ├── AuthController.cs         # Google OAuth login/logout + dev-login
│   ├── CodeQAController.cs       # Upload, Q&A, and project management endpoints
│   └── DashboardController.cs    # Authenticated dashboard API (usage, folders)
├── Models/                       # DTOs and configuration models
├── Services/
│   ├── Analysis/                 # Roslyn + heuristic multi-language analysis services
│   ├── Providers/                # LLM provider implementations (OpenAI, AzureOpenAI)
│   ├── Tools/                    # Agent tools + ReActParser
│   ├── UploadCleanupService.cs   # Background service for expired upload cleanup
│   └── UserStorageService.cs     # Per-user storage management and quota enforcement
├── wwwroot/
│   ├── index.html                # Main Q&A interface
│   ├── dashboard.html            # User dashboard (storage, project management)
│   └── source-code/              # Uploaded source code folders (runtime, gitignored)
└── appsettings.json              # Main configuration
```

## License

See repository for license details.

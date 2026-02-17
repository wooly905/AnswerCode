# AnswerCode

AI-powered code Q&A system. Ask questions about your codebase and get intelligent answers using large language models (LLMs) with an agentic tool-calling loop.

## Features

- **Agentic Q&A**: An AI agent uses tools (grep, read file, list directory, glob search) to explore your codebase and answer questions autonomously
- **Multiple LLM Providers**: Supports OpenAI and Azure OpenAI—add more via configuration
- **Streaming Progress**: Real-time SSE streaming shows tool calls as they happen
- **Dark Theme UI**: Web interface with syntax highlighting, Markdown rendering, and Mermaid diagram support

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

5. The default project path is loaded from configuration (see [Configuration](#configuration)). You can override it in the UI if needed. Choose a model provider and ask a question about your code.

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

LLM providers are configured under the `LLM` section:

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

- **OpenAI** (and other OpenAI-compatible endpoints): Use `Endpoint`, `ApiKey`, `Model`, and optionally `DisplayName`.
- **AzureOpenAI**: Use `Endpoint`, `ApiKey`, `DeploymentName`, and optionally `DisplayName`.

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

## Project Structure

```
AnswerCode/
├── Controllers/       # API controllers
├── Models/            # DTOs and configuration models
├── Services/
│   ├── Providers/     # LLM providers (OpenAI, AzureOpenAI)
│   └── Tools/         # Agent tools (grep, read_file, etc.)
├── wwwroot/           # Static frontend (index.html)
├── appsettings.json   # Main configuration
└── appsettings.Local.json  # Local overrides (gitignored)
```

## License

See repository for license details.

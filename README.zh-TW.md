# AnswerCode

> 🌐 [English](README.md) | **繁體中文**

AI 驅動的程式碼問答系統。透過大型語言模型（LLM）結合主動式工具呼叫迴圈，對您的程式庫提問並獲得智慧解答。

## 功能特色

- **程式碼上傳**：直接在瀏覽器中上傳專案檔案（支援拖放檔案或資料夾，最大 20 MB）— 無需在伺服器端設定路徑
- **主動式問答**：AI 代理使用工具（grep、讀取檔案、列出目錄、glob 搜尋、檔案大綱、定義查找、相關檔案）自主探索程式庫並回答問題
- **雙模式回答**：每個問題可選擇 **開發者** 模式（技術性，附帶檔案路徑與行號）或 **PM** 模式（白話文、以業務為導向、不含程式碼片段）
- **多 LLM 供應商**：可動態設定 — 透過 `appsettings.json` 加入任意數量的 OpenAI 相容、Azure OpenAI 或 Ollama 供應商
- **ReAct 備用迴圈**：不支援原生函式呼叫的供應商會自動切換為基於 `<tool_call>` XML 標籤的文字式 ReAct 迴圈，任何 LLM 皆可作為代理使用
- **串流進度顯示**：即時 SSE 串流，顯示每次工具呼叫的過程，包含結果摘要、可展開的詳細項目與執行時長
- **Token 用量追蹤**：跨所有 LLM 呼叫的輸入與輸出 Token 計數，並顯示於 API 回應與 UI 中
- **多語言專案支援**：自動偵測並摘要 .NET、Node.js、Python、Go、Rust、Java 以及 C/C++ 專案的中繼資料
- **深色主題介面**：網頁介面包含語法上色、Markdown 渲染，以及支援互動式縮放、拖移與全螢幕檢視的 Mermaid 圖表
- **結構化日誌**：透過 Serilog 記錄請求與回應，支援主控台與滾動檔案輸出

## 系統需求

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- LLM API 存取（OpenAI、Azure OpenAI 或 Ollama）

## 快速開始

1. Clone 儲存庫並進入專案資料夾：
   ```bash
   cd AnswerCode
   ```

2. 在 `appsettings.json` 中設定 LLM 供應商（見下方[設定](#設定)）。

3. 執行應用程式：
   ```bash
   dotnet run
   ```

4. 開啟瀏覽器前往 **http://localhost:5000**（或 https://localhost:5001）。

5. 透過拖放區域或「瀏覽檔案」/「瀏覽資料夾」按鈕**上傳您的程式碼**。選擇模型供應商、輸入問題，然後點擊 **以開發者身分回答** 或 **以 PM 身分回答**。

## 回答模式

兩種模式可針對代理行為與回應風格進行調整：

| 模式 | 按鈕 | 目標對象 | 風格 |
|------|------|----------|------|
| **開發者** | 以開發者身分回答 | 工程師 | 技術性；引用檔案路徑、行號、類別/方法名稱與程式碼片段 |
| **PM** | 以 PM 身分回答 | 專案/產品經理 | 白話文；描述業務流程與模組互動，不含程式碼 |

API 請求中的 `UserRole` 欄位（`"Developer"` 或 `"PM"`）用於選擇模式，預設（省略）為 Developer。

## 程式碼上傳

程式碼直接從瀏覽器上傳：

- 點擊 **瀏覽檔案** 選擇個別檔案，或點擊 **瀏覽資料夾** 選擇整個資料夾（保留相對路徑）。
- 將檔案或資料夾拖放至上傳區域。
- 總上傳大小限制為 **20 MB**。
- 上傳完成後，綠色狀態標籤會顯示資料夾 ID 與檔案數量。點擊 **移除** 可從伺服器刪除已上傳的程式碼。
- 支援多筆上傳 — 每次上傳在伺服器的 `wwwroot/source-code/{folderId}/` 下取得唯一的資料夾 ID。

上傳的資料夾 ID 將自動作為所有問答請求的 `projectPath`。

## 設定

所有設定皆透過 `appsettings.json`（或本機覆寫用的 `appsettings.Local.json`）進行設定。

### LLM 供應商

LLM 供應商設定在 `LLM` 區段下。您可以加入任意數量的供應商，每個供應商都會出現在 UI 的供應商下拉選單中。

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

### 供應商類型

- **AzureOpenAI**：使用 `Endpoint`、`ApiKey`、`Model`，以及選填的 `DisplayName`。金鑰必須包含 `azure`（不區分大小寫）。
- **OpenAI / OpenAI 相容**（其他任何金鑰，包括 Ollama）：使用 `Endpoint`、`ApiKey`、`Model`，以及選填的 `DisplayName`。工廠將所有非 AzureOpenAI 金鑰視為 OpenAI 相容供應商 — Ollama 可直接透過其 `/v1/` 端點使用。

### 本機覆寫

使用 `appsettings.Local.json` 進行本機金鑰與設定覆寫。此檔案已加入 gitignore，存在時會覆寫 `appsettings.json` 中的值。從 `appsettings.json` 複製結構並填入您的 API 金鑰。

## 代理工具

代理使用以下工具探索您的程式庫：

| 工具 | 說明 |
|------|------|
| `get_file_outline` | 取得檔案的結構大綱（類別、方法、屬性）與行號 — 比讀取整個檔案更省 Token |
| `find_definition` | 找出符號（類別、介面、方法等）的定義位置 — 比 grep 更精確 |
| `get_related_files` | 找出檔案的相依項目（匯入）與依賴者（引用此檔案的檔案） |
| `grep_search` | 依模式（正規表達式）搜尋檔案內容 |
| `glob_search` | 依名稱模式尋找檔案（例如 `*.cs`） |
| `read_file` | 讀取檔案內容（可指定行號範圍） |
| `list_directory` | 列出子目錄中的檔案（專案根目錄結構已自動注入） |

**自動注入上下文：** 代理在每次對話開始時自動接收專案概覽（目錄結構、語言、框架、相依套件），無需初始 `list_directory` 呼叫，節省一次完整的 LLM 往返。

**多語言專案偵測：** 概覽建構器可自動偵測 `.csproj`（.NET）、`package.json`（Node.js）、`requirements.txt` / `pyproject.toml`（Python）、`go.mod`（Go）、`Cargo.toml`（Rust）、`pom.xml` / `build.gradle`（Java）以及 `CMakeLists.txt` / `Makefile`（C/C++）的專案中繼資料。

## ReAct 備用迴圈

當已設定的供應商回報 `SupportsToolCalling = false` 時，代理會自動切換至 **ReAct 文字迴圈**而非原生函式呼叫。在此模式下：

- LLM 的系統提示中包含嵌入式工具描述。
- 工具呼叫以純文字輸出中的 `<tool_call>{"name": "...", "arguments": {...}}</tool_call>` XML 標籤表示。
- 伺服器透過 `ReActParser` 解析這些標籤、執行工具，並在下一輪以 `<tool_result>` 標籤回傳結果。
- 進度事件與 Token 追蹤的運作方式與原生工具呼叫相同。

這讓任何能產生文字的 LLM 都能作為代理使用，無需支援 OpenAI 風格的函式呼叫。

## API 參考

### POST `/api/codeqa/ask`
同步問答 — 執行代理並在單一回應中回傳完整答案。

**請求內容：**
```json
{
  "question": "驗證功能如何運作？",
  "projectPath": "abd2b91fdb12",
  "modelProvider": "OpenAI",
  "userRole": "Developer",
  "sessionId": "optional-uuid"
}
```

> **`projectPath`** 可以是上傳端點回傳的 `folderId`（例如 `"abd2b91fdb12"`）或伺服器上的絕對路徑。

**回應**（`AnswerResponse`）：
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
串流問答 — 回傳 Server-Sent Events（SSE）。每個事件為 JSON 編碼的 `AgentEvent`。

**事件類型：**

| 類型 | 觸發時機 |
|------|----------|
| `Started` | 代理開始處理 |
| `ToolCallStart` | 工具呼叫即將執行（包含 `toolName`、`toolArgs`、`summary`） |
| `ToolCallEnd` | 工具呼叫已完成（新增 `resultSummary`、`resultDetails`、`detailItems`、`durationMs`） |
| `Answer` | 最終答案已就緒（`result` 包含完整的 `AnswerResponse`） |
| `Error` | 發生錯誤 |

### POST `/api/codeqa/upload`
上傳程式碼檔案。接受包含 `files[]` 與選填 `relativePaths[]` 欄位的 multipart/form-data。

**回應：**
```json
{
  "folderId": "abd2b91fdb12",
  "fileCount": 42,
  "totalSizeBytes": 186366,
  "totalSizeMB": 0.18
}
```

### DELETE `/api/codeqa/upload/{folderId}`
刪除先前上傳的程式碼資料夾。

### GET `/api/codeqa/uploads`
列出所有已上傳的程式碼資料夾（folderId、createdUtc、fileCount）。

### GET `/api/codeqa/providers`
回傳所有已設定且成功初始化的 LLM 供應商的顯示名稱。

### GET `/api/codeqa/structure?projectPath=...`
回傳指定專案路徑的目錄樹（最深 4 層）。

### GET `/api/codeqa/file?filePath=...&maxLines=...&offset=...`
回傳指定檔案的內容，支援選填的分頁參數。

## 專案結構

```
AnswerCode/
├── Controllers/           # API 控制器（CodeQAController）
├── Models/                # DTO 與設定模型
├── Services/
│   ├── Providers/         # LLM 供應商實作（OpenAI、AzureOpenAI）
│   └── Tools/             # 代理工具 + ReActParser
├── wwwroot/               # 靜態前端（index.html）
│   └── source-code/       # 已上傳的程式碼資料夾（執行期，已加入 gitignore）
├── appsettings.json       # 主要設定
└── appsettings.Local.json # 本機覆寫（已加入 gitignore）
```

## 授權

請參閱儲存庫中的授權資訊。

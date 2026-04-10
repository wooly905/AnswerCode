# AnswerCode

> 🌐 [English](README.md) | **繁體中文**

AI 驅動的程式碼問答系統。透過大型語言模型（LLM）結合主動式工具呼叫迴圈，對您的程式庫提問並獲得智慧解答。

## 功能特色

- **程式碼上傳**：直接在瀏覽器中上傳專案檔案（支援拖放檔案或資料夾）— 無需在伺服器端設定路徑
- **Google 登入與持久化儲存**：使用 Google 帳號登入即可獲得專屬的持久化儲存空間（預設 300 MB 配額）— 上傳的專案可跨瀏覽器工作階段保留，並可透過 Dashboard 管理
- **使用者 Dashboard**：已登入使用者可在 `/dashboard` 頁面檢視所有上傳的專案、儲存用量進度條，並可刪除個別專案
- **主動式問答**：AI 代理使用工具（grep、讀取檔案、讀取符號、列出目錄、glob 搜尋、檔案大綱、定義查找、參考查找、測試查找、相關檔案、儲存庫地圖、呼叫圖、網路搜尋、設定查找）自主探索程式庫並回答問題
- **雙模式回答**：每個問題可選擇 **開發者** 模式（技術性，附帶檔案路徑與行號）或 **PM** 模式（白話文、以業務為導向、不含程式碼片段）
- **多 LLM 供應商**：可動態設定 — 透過 `appsettings.json` 加入任意數量的 OpenAI 相容、Azure OpenAI 或 Ollama 供應商
- **ReAct 備用迴圈**：不支援原生函式呼叫的供應商會自動切換為基於 `<tool_call>` XML 標籤的文字式 ReAct 迴圈，任何 LLM 皆可作為代理使用
- **SubAgent 架構**：後續問題採用三階段 SubAgent 設計 —（1）結合對話歷史將後續問題解析為獨立問題，（2）在不帶歷史的情況下執行主動式工具呼叫迴圈以節省 Token，（3）結合歷史上下文合成最終回答。對話歷史長度以 **200K token 預算** 控制（取代固定 turn 數量限制），接近門檻時自動壓縮舊對話
- **對話歷史檢視器**：點擊頂部欄的 **Main** Token 計數器，即可檢視 LLM 實際記憶中的對話內容，並可透過下載按鈕將歷史記錄匯出為 Markdown
- **串流進度顯示**：即時 SSE 串流，顯示每次工具呼叫的過程，包含結果摘要、可展開的詳細項目與執行時長
- **Token 用量追蹤**：主代理（上下文解析 + 合成）與 SubAgent（工具迴圈）的 Token 計數分別追蹤，並在頂部欄以 **Main / Sub / Total** 顯示
- **多語言專案支援**：自動偵測並摘要 .NET、Node.js、Python、Go、Rust、Java 以及 C/C++ 專案的中繼資料
- **混合式多語言程式碼分析**：`C#` 使用 Roslyn 進行精準的符號讀取與參考查找；TypeScript、JavaScript、Python、Go、Rust 使用 LSP 伺服器（typescript-language-server、Pyright、gopls、rust-analyzer）進行語意定義、參考與符號分析，並在 LSP 失敗時自動降級至 heuristic；Java、C/C++ 則使用 heuristic 方式進行符號、參考與測試分析
- **深色主題介面**：網頁介面包含語法上色、Markdown 渲染，以及支援互動式縮放、拖移與全螢幕檢視的 Mermaid 圖表
- **自動清理上傳檔案**：匿名使用者離開頁面時，已上傳的程式碼會透過 `beforeunload` + `sendBeacon` 自動刪除；背景服務作為安全網，根據可設定的 TTL 定期清除過期的上傳資料夾
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

4. 開啟瀏覽器前往 **http://localhost:5000**。

5. 透過拖放區域或「瀏覽檔案」/「瀏覽資料夾」按鈕**上傳您的程式碼**。選擇模型供應商、輸入問題，然後點擊 **以開發者身分回答** 或 **以 PM 身分回答**。

## 回答模式

兩種模式可針對代理行為與回應風格進行調整：

| 模式 | 按鈕 | 目標對象 | 風格 |
|------|------|----------|------|
| **開發者** | 以開發者身分回答 | 工程師 | 技術性；引用檔案路徑、行號、類別/方法名稱與程式碼片段 |
| **PM** | 以 PM 身分回答 | 專案/產品經理 | 白話文；描述業務流程與模組互動，不含程式碼 |

模式可直接透過 UI 中的 **以開發者身分回答** 或 **以 PM 身分回答** 進行選擇。

## 程式碼上傳

程式碼直接從瀏覽器上傳：

- 點擊 **瀏覽檔案** 選擇個別檔案，或點擊 **瀏覽資料夾** 選擇整個資料夾（保留相對路徑）。
- 將檔案或資料夾拖放至上傳區域。
- 支援多筆上傳 — 每次上傳取得唯一的資料夾 ID。
- 上傳完成後，綠色狀態標籤會顯示資料夾 ID 與檔案數量。點擊 **移除** 可從伺服器刪除已上傳的程式碼。

**匿名使用者**（未登入）：
- 上傳大小限制：每次 **20 MB**。
- 檔案儲存於 `wwwroot/source-code/{folderId}/`，關閉瀏覽器分頁時會透過 `navigator.sendBeacon()` 自動刪除。背景服務作為安全網，定期移除超過 TTL（預設 120 分鐘）的上傳資料夾。

**已登入使用者**（透過 Google 登入）：
- 上傳大小限制：每次 **300 MB**，總儲存配額預設為 300 MB（可設定）。
- 檔案儲存於使用者專屬目錄，跨工作階段持久保留。
- 可透過 **Dashboard**（`/dashboard`）管理所有上傳的專案。

上傳的資料夾 ID 將自動作為所有問答請求的 `projectPath`。

## 認證與 Dashboard

AnswerCode 支援選擇性的 Google OAuth 登入。使用問答功能**不需要**認證 — 匿名使用者可如往常般上傳程式碼並提問。

登入後可解鎖：
- **持久化儲存** — 上傳的專案儲存至您的帳號，跨瀏覽器工作階段皆可使用。
- **更大的上傳限制** — 每次可上傳 300 MB（匿名為 20 MB）。
- **Dashboard** — 前往 `/dashboard` 檢視所有上傳的專案、監控儲存用量，並刪除不再需要的專案。

Development 模式下提供 **dev-login** 捷徑（`/api/auth/dev-login`），無需 Google OAuth 憑證即可進行本機測試。

## 設定

所有設定皆透過 `appsettings.json` 進行設定。

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

### Google 認證

Google OAuth 設定在 `Authentication` 區段下。請從 [Google Cloud Console](https://console.cloud.google.com/apis/credentials) 取得 Client ID 與 Client Secret。

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

認證為選擇性功能 — 應用程式在未設定這些憑證的情況下仍可完整運作。

### 使用者儲存配額

已登入使用者的個人儲存上限設定在 `UserStorage` 區段下：

```json
{
  "UserStorage": {
    "MaxSizeMB": 300
  }
}
```

- `MaxSizeMB`：每位使用者的最大儲存容量（MB，預設：300）。

### 網路搜尋（Tavily）

`web_search` 工具使用 [Tavily Search API](https://tavily.com/) 讓代理取得外部資訊。請在 `Tavily` 區段設定 API 金鑰：

```json
{
  "Tavily": {
    "ApiKey": "tvly-your-api-key"
  }
}
```

若未設定 API 金鑰，工具將回傳錯誤訊息，代理會跳過網路搜尋。

### 上傳自動清理

匿名上傳的自動清理設定在 `UploadCleanup` 區段下：

```json
{
  "UploadCleanup": {
    "ScanIntervalMinutes": 10,
    "MaxAgeMinutes": 120
  }
}
```

- `ScanIntervalMinutes`：背景服務掃描過期資料夾的間隔（預設：10 分鐘）。
- `MaxAgeMinutes`：資料夾內檔案超過此時間未被寫入即視為過期並刪除（預設：120 分鐘）。

## 代理工具

代理使用以下工具探索您的程式庫：

| 工具 | 說明 |
|------|------|
| `get_file_outline` | 取得檔案的結構大綱（類別、方法、屬性）與行號 — 比讀取整個檔案更省 Token |
| `find_definition` | 找出符號（類別、介面、方法等）的定義位置 — 比 grep 更精確 |
| `find_references` | 找出符號在整個程式庫中的使用、呼叫、繼承、實作或匯入位置 |
| `find_tests` | 找出與來源符號或檔案相關的可能測試 |
| `get_related_files` | 找出檔案的相依項目（匯入）與依賴者（引用此檔案的檔案） |
| `repo_map` | 產生儲存庫地圖，顯示模組邊界、架構角色、跨模組相依關係、進入點，以及 Mermaid 圖表 |
| `call_graph` | 從方法/函式產生靜態呼叫圖 — 追蹤下游呼叫或上游呼叫者，包含循環偵測與信心標籤 |
| `grep_search` | 依模式（正規表達式）搜尋檔案內容 |
| `glob_search` | 依名稱模式尋找檔案（例如 `*.cs`） |
| `read_file` | 讀取檔案內容（可指定行號範圍） |
| `read_symbol` | 精準讀取單一符號定義，可選擇是否包含主體與註解，避免整個檔案都讀進來 |
| `list_directory` | 列出子目錄中的檔案（專案根目錄結構已自動注入） |
| `web_search` | 透過 Tavily Search API 搜尋網路外部資訊 — 函式庫文件、API 參考、最佳實踐、錯誤說明或最新更新 |
| `config_lookup` | 跨所有設定檔查找設定鍵 — 找出鍵值定義的位置、各來源的值，以及依優先順序哪個值生效。支援 C#、JavaScript、TypeScript、Python、Java、Go、Rust、C/C++ 的設定模式 |

**自動注入上下文：** 代理在每次對話開始時自動接收專案概覽（目錄結構、語言、框架、相依套件），無需初始 `list_directory` 呼叫，節省一次完整的 LLM 往返。

**多語言專案偵測：** 概覽建構器可自動偵測 `.csproj`（.NET）、`package.json`（Node.js）、`requirements.txt` / `pyproject.toml`（Python）、`go.mod`（Go）、`Cargo.toml`（Rust）、`pom.xml` / `build.gradle`（Java）以及 `CMakeLists.txt` / `Makefile`（C/C++）的專案中繼資料。

**符號導向分析：**

- `C#` 的 `read_symbol`、`find_references`、`find_tests`、`call_graph` 走 Roslyn 分析。
- TypeScript、JavaScript、Python 的 `find_definition`、`find_references`、`get_file_outline` 走 LSP 伺服器（`typescript-language-server`、`Pyright`），失敗時自動降級至 heuristic。
- Go、Rust 的相同操作走 LSP 伺服器（`gopls`、`rust-analyzer`），失敗時自動降級至 heuristic。LSP 二進位檔打包於 `lsp-servers/bin/` 下，以便部署至未預裝這些工具的環境（如 Azure App Service）。
- Java、C/C++ 走 heuristic 解析與比對。

## ReAct 備用迴圈

當已設定的供應商回報 `SupportsToolCalling = false` 時，代理會自動切換至 **ReAct 文字迴圈**而非原生函式呼叫。在此模式下：

- LLM 的系統提示中包含嵌入式工具描述。
- 工具呼叫以純文字輸出中的 `<tool_call>{"name": "...", "arguments": {...}}</tool_call>` XML 標籤表示。
- 伺服器透過 `ReActParser` 解析這些標籤、執行工具，並在下一輪以 `<tool_result>` 標籤回傳結果。
- 進度事件與 Token 追蹤的運作方式與原生工具呼叫相同。

這讓任何能產生文字的 LLM 都能作為代理使用，無需支援 OpenAI 風格的函式呼叫。

## SubAgent 架構

當使用者提出後續問題（即存在對話歷史）時，系統會將工作拆分為三個階段以降低 Token 消耗：

| 階段 | 角色 | 是否包含歷史 | LLM 呼叫次數 |
|------|------|-------------|-------------|
| **1. 上下文解析** | 將後續問題解析為獨立問題 | 是 | 1 |
| **2. SubAgent 工具迴圈** | 執行完整的主動式研究迴圈 | **否** | 5–50 |
| **3. 回答合成** | 結合研究結果與對話上下文 | 是 | 1 |

工作階段中的第一個問題（無歷史）會直接進入工具迴圈，零額外開銷。

**為何重要：** 在先前的設計中，對話歷史會隨著工具迴圈中的每次 LLM 呼叫一同傳送（5–50 次）。使用 SubAgent 後，歷史僅傳送兩次（階段 1 + 3），使得 Token 成本幾乎與歷史長度無關。

### Token 預算制歷史管理與自動壓縮

對話歷史不再使用固定 turn 數量限制，改為 **200K token 預算**（以字元數 / 3 估算）。當估算 token 數達到 **180K** 時，系統會自動壓縮較舊的對話：

1. 保留最新 20% 的 turns 原樣不動（至少 1 組問答）。
2. 較舊的 turns 透過 LLM 呼叫摘要為一個精簡的摘要 turn。
3. 壓縮後的歷史會寫回工作階段儲存區。

壓縮支援**鏈式運作** — 當歷史在上次壓縮後再次增長，舊的摘要會被納入下一次壓縮循環。這使得對話可以在 token 預算內無限延續。

頂部欄分別顯示 **Main**（階段 1 + 3）與 **Sub**（階段 2）的 Token 用量。點擊 **Main** 會開啟彈窗顯示 LLM 記憶中的確切對話內容（包含以黃色標示的壓縮摘要 turn），並提供按鈕可將歷史記錄下載為 Markdown。

## 使用體驗補充

- 上傳程式碼後，系統會在 `wwwroot/source-code/{folderId}/` 下建立隔離的工作資料夾。
- UI 會自動沿用目前選取的上傳內容來回答後續問題。
- 較長的回答流程會即時串流顯示進度，包含工具活動、摘要與耗時。
- 最終答案會標示相關檔案與工具使用情況，方便理解代理如何得出結論。

## 專案結構

```
AnswerCode/
├── Controllers/
│   ├── AuthController.cs         # Google OAuth 登入/登出 + dev-login
│   ├── CodeQAController.cs       # 上傳、問答與專案管理端點
│   └── DashboardController.cs    # 已認證的 Dashboard API（用量、資料夾）
├── Models/                       # DTO 與設定模型
├── Services/
│   ├── Analysis/                 # Roslyn + 多語言 heuristic 分析服務
│   ├── Lsp/                      # LSP 客戶端基礎設施（JSON-RPC、伺服器管理）
│   ├── Providers/                # LLM 供應商實作（OpenAI、AzureOpenAI）
│   ├── Tools/                    # 代理工具 + ReActParser
│   ├── UploadCleanupService.cs   # 過期上傳自動清理背景服務
│   └── UserStorageService.cs     # 使用者儲存管理與配額控管
├── lsp-servers/
│   ├── bin/                      # 打包的 LSP 二進位檔（gopls.exe、rust-analyzer.exe）
│   └── node_modules/             # Node.js LSP 伺服器（typescript-language-server、pyright）
├── wwwroot/
│   ├── index.html                # 主要問答介面
│   ├── dashboard.html            # 使用者 Dashboard（儲存用量、專案管理）
│   └── source-code/              # 已上傳的程式碼資料夾（執行期，已加入 gitignore）
└── appsettings.json              # 主要設定
```

## 授權

請參閱儲存庫中的授權資訊。

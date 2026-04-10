using System.Text.Json.Serialization;

namespace AnswerCode.Services.Lsp;

// ── JSON-RPC envelope ────────────────────────────────────────────────────────

internal sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string Jsonrpc { get; set; } = "2.0";
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public object? Params { get; set; }
}

internal sealed class JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")] public string Jsonrpc { get; set; } = "2.0";
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public object? Params { get; set; }
}

// ── Initialize ───────────────────────────────────────────────────────────────

internal sealed class InitializeParams
{
    [JsonPropertyName("processId")] public int? ProcessId { get; set; }
    [JsonPropertyName("rootUri")] public string? RootUri { get; set; }
    [JsonPropertyName("capabilities")] public ClientCapabilities Capabilities { get; set; } = new();
}

internal sealed class ClientCapabilities
{
    [JsonPropertyName("textDocument")] public TextDocumentClientCapabilities TextDocument { get; set; } = new();
    [JsonPropertyName("window")] public WindowClientCapabilities Window { get; set; } = new();
}

internal sealed class TextDocumentClientCapabilities
{
    [JsonPropertyName("documentSymbol")] public DocumentSymbolCapability DocumentSymbol { get; set; } = new();
    [JsonPropertyName("definition")] public DefinitionCapability Definition { get; set; } = new();
    [JsonPropertyName("references")] public ReferencesCapability References { get; set; } = new();
}

internal sealed class DocumentSymbolCapability
{
    [JsonPropertyName("hierarchicalDocumentSymbolSupport")] public bool HierarchicalDocumentSymbolSupport { get; set; } = true;
}

internal sealed class DefinitionCapability
{
    [JsonPropertyName("linkSupport")] public bool LinkSupport { get; set; } = false;
}

internal sealed class ReferencesCapability
{
    [JsonPropertyName("dynamicRegistration")] public bool DynamicRegistration { get; set; } = false;
}

internal sealed class WindowClientCapabilities
{
    [JsonPropertyName("workDoneProgress")] public bool WorkDoneProgress { get; set; } = true;
}

// ── textDocument/documentSymbol ──────────────────────────────────────────────

internal sealed class DocumentSymbolParams
{
    [JsonPropertyName("textDocument")] public TextDocumentIdentifier TextDocument { get; set; } = new();
}

internal sealed class TextDocumentIdentifier
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
}

internal sealed class DocumentSymbol
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("kind")] public int Kind { get; set; }
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
    [JsonPropertyName("selectionRange")] public LspRange SelectionRange { get; set; } = new();
    [JsonPropertyName("children")] public List<DocumentSymbol>? Children { get; set; }
}

// ── textDocument/definition ──────────────────────────────────────────────────

internal sealed class DefinitionParams
{
    [JsonPropertyName("textDocument")] public TextDocumentIdentifier TextDocument { get; set; } = new();
    [JsonPropertyName("position")] public LspPosition Position { get; set; } = new();
}

// ── textDocument/references ──────────────────────────────────────────────────

internal sealed class ReferenceParams
{
    [JsonPropertyName("textDocument")] public TextDocumentIdentifier TextDocument { get; set; } = new();
    [JsonPropertyName("position")] public LspPosition Position { get; set; } = new();
    [JsonPropertyName("context")] public ReferenceContext Context { get; set; } = new();
}

internal sealed class ReferenceContext
{
    [JsonPropertyName("includeDeclaration")] public bool IncludeDeclaration { get; set; } = false;
}

// ── textDocument/didOpen / didClose ──────────────────────────────────────────

internal sealed class DidOpenTextDocumentParams
{
    [JsonPropertyName("textDocument")] public TextDocumentItem TextDocument { get; set; } = new();
}

internal sealed class TextDocumentItem
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    [JsonPropertyName("languageId")] public string LanguageId { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

internal sealed class DidCloseTextDocumentParams
{
    [JsonPropertyName("textDocument")] public TextDocumentIdentifier TextDocument { get; set; } = new();
}

// ── Shared primitives ────────────────────────────────────────────────────────

internal sealed class LspRange
{
    [JsonPropertyName("start")] public LspPosition Start { get; set; } = new();
    [JsonPropertyName("end")] public LspPosition End { get; set; } = new();
}

internal sealed class LspPosition
{
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("character")] public int Character { get; set; }
}

internal sealed class LspLocation
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    [JsonPropertyName("range")] public LspRange Range { get; set; } = new();
}

// ── SymbolKind mapping ───────────────────────────────────────────────────────

internal static class LspSymbolKind
{
    public static string ToKindString(int kind) => kind switch
    {
        1 => "file",
        2 => "module",
        3 => "namespace",
        4 => "package",
        5 => "class",
        6 => "method",
        7 => "property",
        8 => "field",
        9 => "constructor",
        10 => "enum",
        11 => "interface",
        12 => "function",
        13 => "variable",
        14 => "constant",
        15 => "string",
        16 => "number",
        17 => "boolean",
        18 => "array",
        19 => "object",
        20 => "key",
        21 => "null",
        22 => "enumMember",
        23 => "struct",
        24 => "event",
        25 => "operator",
        26 => "typeParameter",
        _ => "unknown"
    };
}

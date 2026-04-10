using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AnswerCode.Services.Lsp;

/// <summary>
/// Hand-written LSP JSON-RPC client using Content-Length header protocol over stdin/stdout.
/// Completely avoids StreamJsonRpc to maintain full control over read/write timing.
/// </summary>
internal sealed class LspJsonRpcClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _input;   // stdout of LSP server (we read from it)
    private readonly Stream _output;  // stdin of LSP server (we write to it)
    private readonly ILogger _logger;

    private int _nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly CancellationTokenSource _readerCts = new();
    private Task? _readerLoop;
    private bool _disposed;

    /// <summary>Fires for every server→client notification (method, params).</summary>
    internal event Action<string, JsonElement?>? OnNotification;

    /// <summary>Fires for every server→client request (id, method, params). Returns null response by default.</summary>
    internal event Func<int, string, JsonElement?, object?>? OnServerRequest;

    internal LspJsonRpcClient(Stream serverStdout, Stream serverStdin, ILogger logger)
    {
        _input = serverStdout;
        _output = serverStdin;
        _logger = logger;
    }

    internal void StartReading()
    {
        _readerLoop = Task.Run(() => ReaderLoopAsync(_readerCts.Token));
    }

    // ── Send ─────────────────────────────────────────────────────────────────

    internal async Task<JsonElement> SendRequestAsync(string method, object? @params, TimeSpan timeout, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        var request = new JsonRpcRequest { Id = id, Method = method, Params = @params };
        await WriteMessageAsync(request, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            // Use WhenAny so we can give a clear timeout error
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, timeoutCts.Token));
            if (completed != tcs.Task)
            {
                _pendingRequests.TryRemove(id, out _);
                throw new TimeoutException($"LSP request '{method}' (id={id}) timed out after {timeout.TotalSeconds}s");
            }

            return await tcs.Task;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _pendingRequests.TryRemove(id, out _);
            throw;
        }
    }

    internal async Task SendNotificationAsync(string method, object? @params, CancellationToken ct = default)
    {
        var notification = new JsonRpcNotification { Method = method, Params = @params };
        await WriteMessageAsync(notification, ct);
    }

    // ── Wire format: Content-Length header + JSON body ────────────────────────

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private async Task WriteMessageAsync(object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message, _serializerOptions);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _writeLock.WaitAsync(ct);
        try
        {
            await _output.WriteAsync(header, ct);
            await _output.WriteAsync(body, ct);
            await _output.FlushAsync(ct);

            var methodName = message switch
            {
                JsonRpcRequest r => r.Method,
                JsonRpcNotification n => n.Method,
                _ => "response",
            };
            _logger.LogDebug("LSP → {Method}: {Json}", methodName, json);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ── Reader loop ──────────────────────────────────────────────────────────

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        // Use a BufferedStream over the raw byte stream.
        // IMPORTANT: Do NOT use StreamReader for the body — Content-Length is in bytes,
        // and StreamReader's internal buffer can consume bytes beyond the header boundary,
        // making it impossible to read exactly N body bytes reliably.
        var buffered = new BufferedStream(_input);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var contentLength = await ReadContentLengthAsync(buffered, ct);
                if (contentLength < 0)
                {
                    break; // stream closed
                }

                // Read exactly contentLength bytes
                var bodyBytes = new byte[contentLength];
                var totalRead = 0;
                while (totalRead < contentLength)
                {
                    var read = await buffered.ReadAsync(bodyBytes.AsMemory(totalRead, contentLength - totalRead), ct);
                    if (read == 0)
                    {
                        break;
                    }

                    totalRead += read;
                }

                if (totalRead < contentLength)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(bodyBytes);
                _logger.LogDebug("LSP ← {Json}", json.Length > 2000 ? json[..2000] + "..." : json);
                DispatchMessage(json);
            }
        }
        catch (OperationCanceledException)
        {
            /* shutdown */
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LSP reader loop crashed");
        }
        finally
        {
            // Fail all pending requests so callers don't hang
            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetException(new IOException("LSP server connection closed"));
                _pendingRequests.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// Read HTTP-style headers from the byte stream line-by-line until the empty line separator.
    /// Returns the Content-Length value, or -1 if stream ended.
    /// </summary>
    private static async Task<int> ReadContentLengthAsync(Stream stream, CancellationToken ct)
    {
        var contentLength = -1;
        var lineBuffer = new List<byte>(128);

        while (true)
        {
            var b = new byte[1];
            var read = await stream.ReadAsync(b.AsMemory(0, 1), ct);
            if (read == 0)
            {
                return -1; // stream ended
            }

            if (b[0] == (byte)'\n')
            {
                // Strip trailing \r if present
                if (lineBuffer.Count > 0 && lineBuffer[^1] == (byte)'\r')
                {
                    lineBuffer.RemoveAt(lineBuffer.Count - 1);
                }

                if (lineBuffer.Count == 0)
                {
                    break; // empty line = end of headers
                }

                var line = Encoding.ASCII.GetString(lineBuffer.ToArray());
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line["Content-Length:".Length..].Trim();
                    if (int.TryParse(value, out var length))
                    {
                        contentLength = length;
                    }
                }

                lineBuffer.Clear();
            }
            else
            {
                lineBuffer.Add(b[0]);
            }
        }

        return contentLength;
    }

    private void DispatchMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var hasId = root.TryGetProperty("id", out var idProp);
            var hasMethod = root.TryGetProperty("method", out var methodProp);

            if (hasId && !hasMethod)
            {
                // Response to our request
                var id = idProp.GetInt32();
                if (_pendingRequests.TryRemove(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                        var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
                        tcs.TrySetException(new InvalidOperationException($"LSP error {code}: {msg}"));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        tcs.TrySetResult(result.Clone());
                    }
                    else
                    {
                        // null result
                        tcs.TrySetResult(default);
                    }
                }
            }
            else if (hasId && hasMethod)
            {
                // Server→client request (e.g. window/workDoneProgress/create)
                var id = idProp.GetInt32();
                var method = methodProp.GetString() ?? "";
                var @params = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?)null;

                _logger.LogDebug("LSP server request: {Method} (id={Id})", method, id);
                object? responseResult = null;
                try
                {
                    responseResult = OnServerRequest?.Invoke(id, method, @params);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error handling server request {Method}", method);
                }

                // Send response back
                _ = SendResponseAsync(id, responseResult);
            }
            else if (hasMethod && !hasId)
            {
                // Notification
                var method = methodProp.GetString() ?? "";
                var @params = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?)null;
                OnNotification?.Invoke(method, @params);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch LSP message");
        }
    }

    private async Task SendResponseAsync(int id, object? result)
    {
        var response = new { jsonrpc = "2.0", id, result };
        try
        {
            await WriteMessageAsync(response, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send response for id={Id}", id);
        }
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _readerCts.CancelAsync();

        if (_readerLoop is not null)
        {
            try
            {
                await _readerLoop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                /* timeout or cancelled — ok */
            }
        }

        _readerCts.Dispose();
        _writeLock.Dispose();
    }
}

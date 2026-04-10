using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AnswerCode.Services.Lsp;

/// <summary>
/// Manages a pool of LSP server instances keyed by (rootPath, serverConfigKey).
/// Automatically evicts idle servers via a background timer.
/// </summary>
public sealed class LspServerManager : ILspServerManager, IDisposable
{
    private readonly LspSettings _settings;
    private readonly ILogger<LspServerManager> _logger;

    /// <summary>Maps language id → config key in <see cref="LspSettings.Servers"/>.</summary>
    private readonly Dictionary<string, string> _languageToConfigKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Active server instances keyed by "rootPath|configKey".</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startLocks = new();
    private readonly ConcurrentDictionary<string, LspServerInstance> _instances = new();

    private readonly Timer _evictionTimer;

    public LspServerManager(IOptions<LspSettings> settings, ILogger<LspServerManager> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        // Build reverse map: languageId → config key
        foreach (var (key, config) in _settings.Servers)
        {
            foreach (var langId in config.LanguageIds)
            {
                _languageToConfigKey[langId] = key;
            }
        }

        // Start idle eviction timer
        var evictInterval = TimeSpan.FromSeconds(Math.Max(30, _settings.IdleTimeoutSeconds / 2.0));
        _evictionTimer = new Timer(_ => _ = EvictIdleServersAsync(), null, evictInterval, evictInterval);
    }

    public bool HasServerFor(string languageId) => _languageToConfigKey.ContainsKey(languageId);

    public async Task<LspServerHandle?> GetServerAsync(string rootPath, string languageId, CancellationToken ct = default)
    {
#pragma warning disable IDE0011 // Add braces
        if (!_languageToConfigKey.TryGetValue(languageId, out var configKey))
            return null;
#pragma warning restore IDE0011 // Add braces

        var instanceKey = $"{rootPath}|{configKey}";

        // Fast path: already running
        if (_instances.TryGetValue(instanceKey, out var existing) && existing.IsReady)
            return new LspServerHandle(existing);

        // Slow path: start under lock
        var startLock = _startLocks.GetOrAdd(instanceKey, _ => new SemaphoreSlim(1, 1));
        await startLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_instances.TryGetValue(instanceKey, out existing) && existing.IsReady)
            {
                return new LspServerHandle(existing);
            }

            // Dispose stale instance if any
            if (existing is not null)
            {
                _instances.TryRemove(instanceKey, out _);
                await existing.DisposeAsync();
            }

            var config = _settings.Servers[configKey];
            var timeout = TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds);
            var instance = new LspServerInstance(config, rootPath, timeout, _logger);

            try
            {
                await instance.StartAsync(ct);
                _instances[instanceKey] = instance;
                return new LspServerHandle(instance);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start LSP server {Key} for {Root}", configKey, rootPath);
                await instance.DisposeAsync();
                return null;
            }
        }
        finally
        {
            startLock.Release();
        }
    }

    public async Task EvictAsync(string rootPath)
    {
        var toRemove = _instances.Where(kvp => kvp.Key.StartsWith(rootPath + "|", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var (key, instance) in toRemove)
        {
            _instances.TryRemove(key, out _);
            _logger.LogInformation("Evicting LSP server: {Key}", key);
            await instance.DisposeAsync();
        }
    }

    private async Task EvictIdleServersAsync()
    {
        var threshold = DateTime.UtcNow.AddSeconds(-_settings.IdleTimeoutSeconds);
        var toRemove = _instances.Where(kvp => kvp.Value.LastUsed < threshold || !kvp.Value.IsReady).ToList();

        foreach (var (key, instance) in toRemove)
        {
            _instances.TryRemove(key, out _);
            _logger.LogInformation("Evicting idle LSP server: {Key} (last used {Ago}s ago)", key, (DateTime.UtcNow - instance.LastUsed).TotalSeconds);
            try
            {
                await instance.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing LSP server {Key}", key); }
        }
    }

    // ── IAsyncDisposable + IDisposable ───────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _evictionTimer.Dispose();
        foreach (var (_, instance) in _instances)
        {
            try { await instance.DisposeAsync(); }
            catch { /* best effort */ }
        }
        _instances.Clear();
    }

    public void Dispose()
    {
        _evictionTimer.Dispose();
        foreach (var (_, instance) in _instances)
        {
            try { instance.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* best effort */ }
        }
        _instances.Clear();
    }
}

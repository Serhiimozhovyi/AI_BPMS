using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace OpenBP.Mcp.Client;

/// <summary>
/// Реєстр MCP-серверів на офіційному C# SDK: stdio і Streamable HTTP, кеш <c>tools/list</c>,
/// пул сесій (одна сесія на сервер) з таймаутами підключення, викликів і простою.
/// Сесія, чий транспорт закрився (процес упав, HTTP-з'єднання втрачено), прибирається з пулу
/// і перевідкривається при наступному виклику.
/// </summary>
public sealed class McpServerRegistry : IMcpServerRegistry, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _servers = new(StringComparer.Ordinal);
    private readonly McpRegistryOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly TimeProvider _time;
    private long _lastSweep;
    private bool _disposed;

    /// <summary>Створює порожній реєстр.</summary>
    /// <param name="options">Таймаути; <c>null</c> — за замовчуванням.</param>
    /// <param name="loggerFactory">Логи SDK; <c>null</c> — без логів.</param>
    /// <param name="timeProvider">Годинник для простою сесій і <see cref="ToolsCache.CachedAt"/>.</param>
    public McpServerRegistry(McpRegistryOptions? options = null, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new McpRegistryOptions();
        _loggerFactory = loggerFactory;
        _time = timeProvider ?? TimeProvider.System;
        _lastSweep = _time.GetTimestamp();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<McpServerConfig> Servers => [.. _servers.Values.Select(e => e.Config)];

    /// <summary>Кількість відкритих сесій у пулі (для діагностики і тестів).</summary>
    public int OpenSessionCount => _servers.Values.Count(e => e.Session is { IsClosed: false });

    /// <inheritdoc />
    public async Task RegisterAsync(McpServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateConfig(config);

        var entry = new Entry(config);
        Entry? previous = null;
        _servers.AddOrUpdate(config.Id, entry, (_, old) =>
        {
            previous = old;
            return entry;
        });

        if (previous is not null)
        {
            await CloseAsync(previous).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> UnregisterAsync(string serverId)
    {
        if (!_servers.TryRemove(serverId, out var entry))
        {
            return false;
        }

        await CloseAsync(entry).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public bool TryGetServer(string serverId, [NotNullWhen(true)] out McpServerConfig? config)
    {
        config = _servers.TryGetValue(serverId, out var entry) ? entry.Config : null;
        return config is not null;
    }

    /// <inheritdoc />
    public ToolsCache? GetToolsCache(string serverId) =>
        _servers.TryGetValue(serverId, out var entry) ? entry.Cache : null;

    /// <inheritdoc />
    public void SetToolsCache(string serverId, ToolsCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        GetEntry(serverId).Cache = cache;
    }

    /// <inheritdoc />
    public async Task<ToolsCache> RefreshToolsAsync(string serverId, CancellationToken ct = default)
    {
        var entry = GetEntry(serverId);
        var tools = await RunAsync(
            serverId,
            async (client, token) => await client.ListToolsAsync(cancellationToken: token).ConfigureAwait(false),
            timeout: null,
            ct).ConfigureAwait(false);

        var cache = new ToolsCache([.. tools.Select(t => ToInfo(t.ProtocolTool))], _time.GetUtcNow());
        entry.Cache = cache;
        return cache;
    }

    /// <inheritdoc />
    public async Task<T> RunAsync<T>(
        string serverId,
        Func<McpClient, CancellationToken, Task<T>> action,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = GetEntry(serverId);
        SweepIdleInBackground();

        var limit = timeout ?? _options.RequestTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);

        Session session;
        try
        {
            session = await AcquireAsync(entry, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw Timeout(entry.Config, limit, ex);
        }

        try
        {
            var call = action(session.Client, cts.Token);
            var finished = await Task.WhenAny(call, session.Client.Completion).ConfigureAwait(false);
            if (finished != call)
            {
                // Транспорт закрився раніше, ніж прийшла відповідь: не чекаємо таймауту.
                _ = call.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                await DropAsync(entry, session).ConfigureAwait(false);
                throw ConnectionLost(entry.Config, session, null);
            }

            return await call.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            if (session.IsClosed)
            {
                await DropAsync(entry, session).ConfigureAwait(false);
            }

            throw Timeout(entry.Config, limit, ex);
        }
        catch (Exception ex) when (IsConnectionFailure(ex, session))
        {
            await DropAsync(entry, session).ConfigureAwait(false);
            throw ConnectionLost(entry.Config, session, ex);
        }
        finally
        {
            Interlocked.Decrement(ref session.InUse);
            Interlocked.Exchange(ref session.LastUsed, _time.GetTimestamp());
        }
    }

    /// <inheritdoc />
    public async Task<McpClient> OpenSessionAsync(string serverId, string? protocolVersion = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = GetEntry(serverId);
        if (entry.Removed)
        {
            throw new McpServerUnknownException(serverId);
        }

        return await ConnectAsync(entry.Config, ct, protocolVersion).ConfigureAwait(false);
    }

    /// <summary>Закриває сесії без викликів довше за <see cref="McpRegistryOptions.IdleTimeout"/>.</summary>
    /// <returns>Кількість закритих сесій.</returns>
    public async Task<int> EvictIdleAsync()
    {
        var closed = 0;
        foreach (var entry in _servers.Values)
        {
            if (!await entry.Gate.WaitAsync(0).ConfigureAwait(false))
            {
                continue;
            }

            Session? idle = null;
            try
            {
                var session = entry.Session;
                if (session is not null
                    && Volatile.Read(ref session.InUse) == 0
                    && _time.GetElapsedTime(Interlocked.Read(ref session.LastUsed)) >= _options.IdleTimeout)
                {
                    entry.Session = null;
                    idle = session;
                }
            }
            finally
            {
                entry.Gate.Release();
            }

            if (idle is not null)
            {
                await DisposeQuietlyAsync(idle).ConfigureAwait(false);
                closed++;
            }
        }

        return closed;
    }

    /// <summary>Закриває всі сесії (stdio-процеси завершуються).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _servers.Values)
        {
            await CloseAsync(entry).ConfigureAwait(false);
        }

        _servers.Clear();
    }

    private static void ValidateConfig(McpServerConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Id);
        switch (config.Transport)
        {
            case McpTransportKind.Stdio when string.IsNullOrWhiteSpace(config.Command):
                throw new ArgumentException($"stdio-сервер «{config.Id}» потребує Command.", nameof(config));
            case McpTransportKind.Http when config.Url is null || !config.Url.IsAbsoluteUri:
                throw new ArgumentException($"http-сервер «{config.Id}» потребує абсолютний Url.", nameof(config));
            case McpTransportKind.Stdio or McpTransportKind.Http:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(config), config.Transport, "Невідомий транспорт.");
        }
    }

    private static McpToolInfo ToInfo(Tool tool) => new(
        tool.Name,
        tool.Title,
        tool.Description,
        ToObject(tool.InputSchema) ?? new JsonObject { ["type"] = "object" },
        tool.OutputSchema is { } output ? ToObject(output) : null);

    private static JsonObject? ToObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? JsonNode.Parse(element.GetRawText()) as JsonObject : null;

    /// <summary>
    /// Чи означає виняток втрату з'єднання. JSON-RPC помилки сервера (<see cref="McpProtocolException"/>) —
    /// ні: сервер живий, але відмовив.
    /// </summary>
    private static bool IsConnectionFailure(Exception ex, Session session) =>
        ex is not McpUnavailableException and not McpProtocolException and not OperationCanceledException
        && (session.IsClosed || ex is McpException or IOException or HttpRequestException or ObjectDisposedException);

    private static McpUnavailableException Timeout(McpServerConfig config, TimeSpan limit, Exception? inner) =>
        new(config.Id, $"MCP-сервер «{config.Name}» не відповів за {limit.TotalSeconds:0.###} с.", inner);

    private static McpUnavailableException ConnectionLost(McpServerConfig config, Session session, Exception? inner)
    {
        var transportError = session.Client.Completion is { IsCompletedSuccessfully: true } completion
            ? completion.Result.Exception
            : session.Client.Completion.Exception?.InnerException;
        var reason = transportError?.Message ?? inner?.Message;
        return new(
            config.Id,
            $"З'єднання з MCP-сервером «{config.Name}» обірвалося{(reason is null ? "." : $": {reason}")}",
            inner ?? transportError);
    }

    private static async Task DisposeQuietlyAsync(Session session)
    {
        try
        {
            await session.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Сесія вже мертва або процес не завершився вчасно — пул її все одно забуває.
        }
    }

    private Entry GetEntry(string serverId)
    {
        ArgumentNullException.ThrowIfNull(serverId);
        return _servers.TryGetValue(serverId, out var entry) ? entry : throw new McpServerUnknownException(serverId);
    }

    private async Task<Session> AcquireAsync(Entry entry, CancellationToken ct)
    {
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.Removed)
            {
                throw new McpServerUnknownException(entry.Config.Id);
            }

            var session = entry.Session;
            if (session is { IsClosed: true })
            {
                entry.Session = null;
                await DisposeQuietlyAsync(session).ConfigureAwait(false);
                session = null;
            }

            if (session is null)
            {
                session = new Session(await ConnectAsync(entry.Config, ct).ConfigureAwait(false), _time.GetTimestamp());
                entry.Session = session;
            }

            Interlocked.Increment(ref session.InUse);
            Interlocked.Exchange(ref session.LastUsed, _time.GetTimestamp());
            return session;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task<McpClient> ConnectAsync(McpServerConfig config, CancellationToken ct, string? protocolVersion = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.ConnectTimeout);
        try
        {
            var options = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "AI_BPMS", Version = typeof(McpServerRegistry).Assembly.GetName().Version?.ToString() ?? "0.0.0" },
                InitializationTimeout = _options.ConnectTimeout,
                // Проба `server/discover` за замовчуванням чекає 5 с, потім клієнт відкочується на `initialize`. Сервер,
                // що відповів на discover пізніше (повільний старт stdio-процесу, навантаження), уже на протоколі
                // 2026-07-28 і відхиляє `initialize` — тож проба чекає стільки ж, скільки все підключення.
                DiscoverProbeTimeout = _options.ConnectTimeout,
                ProtocolVersion = protocolVersion,
            };
            return await McpClient.CreateAsync(CreateTransport(config), options, _loggerFactory, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new McpUnavailableException(
                config.Id, $"MCP-сервер «{config.Name}» не підключився за {_options.ConnectTimeout.TotalSeconds:0.###} с.", ex);
        }
        catch (Exception ex)
        {
            throw new McpUnavailableException(config.Id, $"Не вдалося підключитися до MCP-сервера «{config.Name}»: {ex.Message}", ex);
        }
    }

    private IClientTransport CreateTransport(McpServerConfig config) => config.Transport switch
    {
        McpTransportKind.Stdio => new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command!,
                Arguments = [.. config.Arguments ?? []],
                WorkingDirectory = config.WorkingDirectory,
                EnvironmentVariables = config.Environment?.ToDictionary(),
                ShutdownTimeout = _options.ShutdownTimeout,
            },
            _loggerFactory),
        _ => new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = config.Name,
                Endpoint = config.Url!,
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = _options.ConnectTimeout,
                AdditionalHeaders = config.Headers?.ToDictionary(),
            },
            _loggerFactory),
    };

    private async Task DropAsync(Entry entry, Session session)
    {
        if (Interlocked.CompareExchange(ref entry.Session, null, session) == session)
        {
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    private async Task CloseAsync(Entry entry)
    {
        entry.Removed = true;
        if (Interlocked.Exchange(ref entry.Session, null) is { } session)
        {
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    private void SweepIdleInBackground()
    {
        var last = Interlocked.Read(ref _lastSweep);
        if (_time.GetElapsedTime(last) < _options.IdleTimeout / 4
            || Interlocked.CompareExchange(ref _lastSweep, _time.GetTimestamp(), last) != last)
        {
            return;
        }

        _ = Task.Run(EvictIdleAsync);
    }

    private sealed class Entry(McpServerConfig config)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Session? Session;
        public volatile ToolsCache? Cache;
        public volatile bool Removed;

        public McpServerConfig Config { get; } = config;
    }

    private sealed class Session(McpClient client, long timestamp)
    {
        public int InUse;
        public long LastUsed = timestamp;

        public McpClient Client { get; } = client;

        public bool IsClosed => Client.Completion.IsCompleted;
    }
}

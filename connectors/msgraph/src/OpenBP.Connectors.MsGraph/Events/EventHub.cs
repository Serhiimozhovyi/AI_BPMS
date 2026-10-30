using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.MsGraph.Events;

/// <summary>
/// Стрічка подій конектора — ресурс для push-тригера <c>mcp.subscribe</c> AI_BPMS (docs/connectors.md §9.2).
/// <see cref="Uri"/> — сталий (<c>telegram://updates</c>) або шаблон із параметрами в дужках (<c>kit://changes/{entity}</c>):
/// тоді підписуються на конкретний URI, і в кожного свій буфер і своє джерело.
/// </summary>
public sealed class EventFeed
{
    /// <summary>URI або шаблон URI ресурсу.</summary>
    public required string Uri { get; init; }

    /// <summary>Назва для <c>resources/list</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Опис для людей (що за події, який <c>keyField</c>).</summary>
    public string? Description { get; init; }

    /// <summary>Скільки останніх подій тримає буфер кожного URI.</summary>
    public int Capacity { get; init; } = 200;

    /// <summary>
    /// Перевірка перед підпискою й читанням: текст помилки (ресурс зараз недоступний, напр. у бота заданий webhook) або
    /// <c>null</c>. Помилка повертається клієнту як помилка протоколу з цим текстом.
    /// </summary>
    public Func<EventSink, CancellationToken, Task<string?>>? Check { get; init; }

    /// <summary>
    /// Джерело подій: запускається, коли на URI з'являється перший підписник, і скасовується, коли йде останній.
    /// Публікує події через <see cref="EventSink.PublishAsync(JsonObject, CancellationToken)"/>; виняток — пауза й перезапуск (до 1 хв).
    /// <c>null</c> — події публікує сам конектор (<see cref="EventHub.PublishAsync(string, JsonObject, CancellationToken)"/>).
    /// </summary>
    public Func<EventSink, CancellationToken, Task>? Source { get; init; }

    /// <summary>Чи це шаблон URI.</summary>
    public bool IsTemplate => Uri.Contains('{', StringComparison.Ordinal);

    private Regex? _pattern;

    /// <summary>Параметри шаблону з <paramref name="uri"/> або <c>null</c>, якщо URI не цієї стрічки.</summary>
    internal IReadOnlyDictionary<string, string>? Match(string uri)
    {
        if (!IsTemplate)
        {
            return string.Equals(uri, Uri, StringComparison.Ordinal) ? new Dictionary<string, string>() : null;
        }

        _pattern ??= new Regex(
            "^" + Regex.Replace(Regex.Escape(Uri), @"\\\{(\w+)}", "(?<$1>[^/{}]+)") + "$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        var match = _pattern.Match(uri);
        return match.Success
            ? _pattern.GetGroupNames().Where(n => !int.TryParse(n, out _)).ToDictionary(n => n, n => System.Uri.UnescapeDataString(match.Groups[n].Value))
            : null;
    }
}

/// <summary>Конкретний URI стрічки: куди джерело публікує події.</summary>
public sealed class EventSink
{
    private readonly EventHub _hub;

    internal EventSink(EventHub hub, EventFeed feed, string uri, IReadOnlyDictionary<string, string> parameters)
    {
        _hub = hub;
        Feed = feed;
        Uri = uri;
        Parameters = parameters;
    }

    /// <summary>Стрічка.</summary>
    public EventFeed Feed { get; }

    /// <summary>Конкретний URI.</summary>
    public string Uri { get; }

    /// <summary>Параметри шаблону (<c>entity</c> → <c>sales</c>); для сталого URI — порожньо.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Додає подію в буфер і сповіщає підписників.</summary>
    public Task<int> PublishAsync(JsonObject @event, CancellationToken ct) => _hub.PublishAsync(Uri, @event, ct);

    /// <summary>Додає кілька подій і надсилає одне сповіщення.</summary>
    public Task<int> PublishAsync(IEnumerable<JsonObject> events, CancellationToken ct) => _hub.PublishAsync(Uri, events, ct);
}

/// <summary>
/// Ресурси подій MCP-сервера конектора (docs/connectors.md §9.2): <c>resources/list</c> і <c>resources/templates/list</c>
/// зі стрічок, <c>resources/read</c> віддає буфер <c>{ "result": [...] }</c> (від старих до нових), <c>resources/subscribe</c>
/// / <c>unsubscribe</c> і <c>notifications/resources/updated</c> усім підписаним сесіям на кожну нову порцію подій.
/// Сповіщення — лише сигнал: AI_BPMS після нього читає ресурс і сам відсікає вже бачені події за <c>keyField</c>.
/// Для Streamable HTTP сервер має тримати сесії (<c>HttpServerSessionMode.StatefulForInitializeClients</c>): у stateless
/// режимі сповіщення нікуди надіслати.
/// </summary>
public sealed partial class EventHub(IEnumerable<EventFeed> feeds, ILogger<EventHub> logger) : IAsyncDisposable
{
    /// <summary>Тип вмісту ресурсу.</summary>
    public const string MimeType = "application/json";

    private static readonly TimeSpan MaxRestartDelay = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<EventFeed> _feeds = [.. feeds];
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);

    /// <summary>Стрічки сервера.</summary>
    public IReadOnlyList<EventFeed> Feeds => _feeds;

    /// <summary>Скільки сесій підписано на <paramref name="uri"/>.</summary>
    public int SubscriberCount(string uri)
    {
        lock (_gate)
        {
            return _channels.TryGetValue(uri, out var channel) ? channel.Subscribers.Count : 0;
        }
    }

    /// <summary>Чи працює зараз джерело подій для <paramref name="uri"/>.</summary>
    public bool IsSourceRunning(string uri)
    {
        lock (_gate)
        {
            return _channels.TryGetValue(uri, out var channel) && channel.Source is { IsCompleted: false };
        }
    }

    /// <summary>Додає подію до буфера <paramref name="uri"/> і сповіщає підписників; повертає, скільком сесіям надіслано.</summary>
    public Task<int> PublishAsync(string uri, JsonObject @event, CancellationToken ct) => PublishAsync(uri, [@event], ct);

    /// <summary>Додає події до буфера <paramref name="uri"/> і сповіщає підписників одним сповіщенням.</summary>
    /// <exception cref="ArgumentException">URI не належить жодній стрічці.</exception>
    public async Task<int> PublishAsync(string uri, IEnumerable<JsonObject> events, CancellationToken ct)
    {
        McpServer[] subscribers;
        lock (_gate)
        {
            var channel = Get(uri) ?? throw new ArgumentException($"Ресурс {uri} не належить жодній стрічці подій.", nameof(uri));
            var added = 0;
            foreach (var e in events)
            {
                channel.Events.Add(e);
                added++;
            }

            if (added == 0)
            {
                return 0;
            }

            if (channel.Events.Count > channel.Sink.Feed.Capacity)
            {
                channel.Events.RemoveRange(0, channel.Events.Count - channel.Sink.Feed.Capacity);
            }

            subscribers = [.. channel.Subscribers.Values];
        }

        var notified = 0;
        foreach (var server in subscribers)
        {
            try
            {
                await server.SendNotificationAsync(
                    NotificationMethods.ResourceUpdatedNotification,
                    new ResourceUpdatedNotificationParams { Uri = uri },
                    cancellationToken: ct).ConfigureAwait(false);
                notified++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Сесію закрито: прибираємо підписника (і зупиняємо джерело, якщо він був останнім).
                LogNotifyFailed(logger, uri, ex.Message);
                await RemoveAsync(uri, server).ConfigureAwait(false);
            }
        }

        return notified;
    }

    /// <summary>Вміст ресурсу: <c>{ "result": [...] }</c>.</summary>
    /// <exception cref="ArgumentException">URI не належить жодній стрічці.</exception>
    public string ReadJson(string uri)
    {
        lock (_gate)
        {
            var channel = Get(uri) ?? throw new ArgumentException($"Ресурс {uri} не належить жодній стрічці подій.", nameof(uri));
            return new JsonObject { ["result"] = new JsonArray([.. channel.Events.Select(e => e.DeepClone())]) }.ToJsonString();
        }
    }

    /// <summary>Підписує сесію на <paramref name="uri"/>; перший підписник запускає джерело.</summary>
    public async Task SubscribeAsync(McpServer server, string uri, CancellationToken ct)
    {
        var sink = await CheckAsync(uri, ct).ConfigureAwait(false);
        lock (_gate)
        {
            var channel = Get(uri)!;
            // Сесію впізнаємо за SessionId: у Streamable HTTP кожен запит має власний об'єкт McpServer тієї самої сесії.
            channel.Subscribers[SessionKey(server)] = server;

            if (sink.Feed.Source is not null && channel.Source is null or { IsCompleted: true })
            {
                channel.Stop = new CancellationTokenSource();
                channel.Source = RunSourceAsync(channel.Sink, channel.Stop.Token);
            }
        }
    }

    /// <summary>Відписує сесію; останній відписаний зупиняє джерело (буфер лишається).</summary>
    public Task UnsubscribeAsync(McpServer server, string uri) => RemoveAsync(uri, server);

    /// <summary>Перевіряє URI (<see cref="EventFeed.Check"/>) і повертає його канал.</summary>
    /// <exception cref="McpProtocolException">Невідомий URI або ресурс зараз недоступний.</exception>
    private async Task<EventSink> CheckAsync(string uri, CancellationToken ct)
    {
        EventSink sink;
        lock (_gate)
        {
            sink = Get(uri)?.Sink ?? throw new McpProtocolException($"Ресурс {uri} не знайдено.", McpErrorCode.InvalidParams);
        }

        if (sink.Feed.Check is { } check && await check(sink, ct).ConfigureAwait(false) is { } error)
        {
            throw new McpProtocolException(error, McpErrorCode.InvalidRequest);
        }

        return sink;
    }

    /// <summary>
    /// Прибирає підписника; останній зупиняє джерело. Джерело лише скасовується, без очікування: його може зупиняти саме
    /// джерело (сповіщення з <see cref="PublishAsync(string, IEnumerable{JsonObject}, CancellationToken)"/> до закритої сесії).
    /// </summary>
    private Task RemoveAsync(string uri, McpServer server)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(uri, out var channel) || !channel.Subscribers.Remove(SessionKey(server)) || channel.Subscribers.Count > 0)
            {
                return Task.CompletedTask;
            }

            channel.Stop?.Cancel();
            channel.Stop = null;
            channel.Source = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>Канал URI (створюється на першому зверненні); <c>null</c> — URI не належить жодній стрічці. Під <see cref="_gate"/>.</summary>
    private Channel? Get(string uri)
    {
        if (_channels.TryGetValue(uri, out var channel))
        {
            return channel;
        }

        foreach (var feed in _feeds)
        {
            if (feed.Match(uri) is { } parameters)
            {
                channel = new Channel(new EventSink(this, feed, uri, parameters));
                _channels[uri] = channel;
                return channel;
            }
        }

        return null;
    }

    private async Task RunSourceAsync(EventSink sink, CancellationToken ct)
    {
        await Task.Yield();
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await sink.Feed.Source!(sink, ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogSourceFailed(logger, sink.Uri, ex.Message);
            }

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRestartDelay.Ticks));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<Task> running = [];
        lock (_gate)
        {
            foreach (var channel in _channels.Values)
            {
                channel.Stop?.Cancel();
                if (channel.Source is not null)
                {
                    running.Add(channel.Source);
                }
            }
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>
    /// Реєструє стрічки в MCP-сервері: capability <c>resources.subscribe</c> і обробники list/templates/read/subscribe/unsubscribe.
    /// Стрічки — <see cref="EventFeed"/> у DI (<c>services.AddSingleton(new EventFeed { … })</c> або фабрикою).
    /// </summary>
    /// <param name="services">Сервіси конектора.</param>
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton<EventHub>();
        services.PostConfigure<McpServerOptions>(options =>
        {
            options.Capabilities ??= new ServerCapabilities();
            options.Capabilities.Resources ??= new ResourcesCapability();
            options.Capabilities.Resources.Subscribe = true;
            options.Handlers.ListResourcesHandler = (context, _) => ValueTask.FromResult(new ListResourcesResult
            {
                Resources = [.. Hub(context).Feeds.Where(f => !f.IsTemplate)
                    .Select(f => new Resource { Uri = f.Uri, Name = f.Name, Description = f.Description, MimeType = MimeType })],
            });
            options.Handlers.ListResourceTemplatesHandler = (context, _) => ValueTask.FromResult(new ListResourceTemplatesResult
            {
                ResourceTemplates = [.. Hub(context).Feeds.Where(f => f.IsTemplate)
                    .Select(f => new ResourceTemplate { UriTemplate = f.Uri, Name = f.Name, Description = f.Description, MimeType = MimeType })],
            });
            options.Handlers.ReadResourceHandler = async (context, ct) =>
            {
                var uri = context.Params?.Uri ?? "";
                var hub = Hub(context);
                await hub.CheckAsync(uri, ct).ConfigureAwait(false);
                return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = MimeType, Text = hub.ReadJson(uri) }] };
            };
            options.Handlers.SubscribeToResourcesHandler = async (context, ct) =>
            {
                await Hub(context).SubscribeAsync(context.Server, context.Params?.Uri ?? "", ct).ConfigureAwait(false);
                return new EmptyResult();
            };
            options.Handlers.UnsubscribeFromResourcesHandler = async (context, _) =>
            {
                await Hub(context).UnsubscribeAsync(context.Server, context.Params?.Uri ?? "").ConfigureAwait(false);
                return new EmptyResult();
            };
        });
    }

    /// <summary>Ключ сесії: <c>SessionId</c> (HTTP) або один на процес (stdio — одна сесія).</summary>
    private static string SessionKey(McpServer server) => server.SessionId ?? "stdio";

    private static EventHub Hub<T>(RequestContext<T> context) => context.Services!.GetRequiredService<EventHub>();

    private sealed class Channel(EventSink sink)
    {
        public EventSink Sink { get; } = sink;
        public List<JsonObject> Events { get; } = [];
        public Dictionary<string, McpServer> Subscribers { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource? Stop { get; set; }
        public Task? Source { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Сповіщення про {Uri} не надіслано: {Reason}")]
    private static partial void LogNotifyFailed(ILogger logger, string uri, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Джерело подій {Uri} впало, перезапуск: {Reason}")]
    private static partial void LogSourceFailed(ILogger logger, string uri, string reason);
}

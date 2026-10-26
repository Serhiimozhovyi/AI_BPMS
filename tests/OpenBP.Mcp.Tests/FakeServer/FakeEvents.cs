using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenBP.Mcp.FakeServer;

/// <summary>
/// Події FakeServer для push-тригера <c>mcp.subscribe</c> (F6-13): ресурс <see cref="Uri"/> віддає останні події як
/// <c>{ "result": [ { id, payload, at } ] }</c>, tool <c>emit_event</c> додає подію й шле
/// <c>notifications/resources/updated</c> усім сесіям, що підписалися на ресурс (<c>resources/subscribe</c>).
/// Стан — на екземпляр сервера (DI), тож кожен <see cref="FakeServerApp.BuildHttp"/> має власний.
/// </summary>
public sealed class FakeEvents
{
    /// <summary>URI ресурсу подій.</summary>
    public const string Uri = "fake://events";

    /// <summary>Скільки останніх подій віддає ресурс.</summary>
    public const int Capacity = 100;

    private readonly Lock _gate = new();
    private readonly List<JsonObject> _events = [];
    private readonly List<McpServer> _subscribers = [];

    /// <summary>Остання помилка надсилання сповіщення (для діагностики в тестах).</summary>
    public string? LastError { get; private set; }

    /// <summary>Скільки сесій зараз підписано на ресурс.</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>Додає подію і сповіщає підписників.</summary>
    /// <param name="id">Ключ події.</param>
    /// <param name="payload">Довільні дані події.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<int> EmitAsync(string id, JsonNode? payload, CancellationToken ct)
    {
        McpServer[] subscribers;
        lock (_gate)
        {
            _events.Add(new JsonObject
            {
                ["id"] = id,
                ["payload"] = payload?.DeepClone(),
                ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            });
            if (_events.Count > Capacity)
            {
                _events.RemoveRange(0, _events.Count - Capacity);
            }

            subscribers = [.. _subscribers];
        }

        var notified = 0;
        foreach (var server in subscribers)
        {
            try
            {
                await server.SendNotificationAsync(
                    NotificationMethods.ResourceUpdatedNotification,
                    new ResourceUpdatedNotificationParams { Uri = Uri },
                    cancellationToken: ct);
                notified++;
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                // Сесія вже закрита: прибираємо підписника.
                lock (_gate)
                {
                    _subscribers.Remove(server);
                }
            }
        }

        return notified;
    }

    /// <summary>Поточний вміст ресурсу.</summary>
    public string ReadJson()
    {
        lock (_gate)
        {
            return new JsonObject { ["result"] = new JsonArray([.. _events.Select(e => e.DeepClone())]) }.ToJsonString();
        }
    }

    /// <summary>Реєструє ресурс, підписки й tools подій у сервері.</summary>
    /// <param name="services">Сервіси застосунку FakeServer.</param>
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeEvents>();
        services.PostConfigure<McpServerOptions>(options =>
        {
            options.Capabilities ??= new ServerCapabilities();
            options.Capabilities.Resources ??= new ResourcesCapability();
            options.Capabilities.Resources.Subscribe = true;
            options.Handlers.ListResourcesHandler = (_, _) => ValueTask.FromResult(new ListResourcesResult
            {
                Resources = [new Resource { Uri = Uri, Name = "events", Title = "Події", MimeType = "application/json" }],
            });
            options.Handlers.ReadResourceHandler = (context, _) =>
            {
                if (context.Params?.Uri != Uri)
                {
                    throw new McpProtocolException($"Ресурс {context.Params?.Uri} не знайдено.", McpErrorCode.InvalidParams);
                }

                var events = context.Services!.GetRequiredService<FakeEvents>();
                return ValueTask.FromResult(new ReadResourceResult
                {
                    Contents = [new TextResourceContents { Uri = Uri, MimeType = "application/json", Text = events.ReadJson() }],
                });
            };
            options.Handlers.SubscribeToResourcesHandler = (context, _) =>
            {
                if (context.Params?.Uri != Uri)
                {
                    throw new McpProtocolException($"Ресурс {context.Params?.Uri} не знайдено.", McpErrorCode.InvalidParams);
                }

                var events = context.Services!.GetRequiredService<FakeEvents>();
                lock (events._gate)
                {
                    if (!events._subscribers.Contains(context.Server))
                    {
                        events._subscribers.Add(context.Server);
                    }
                }

                return ValueTask.FromResult(new EmptyResult());
            };
            options.Handlers.UnsubscribeFromResourcesHandler = (context, _) =>
            {
                var events = context.Services!.GetRequiredService<FakeEvents>();
                lock (events._gate)
                {
                    events._subscribers.Remove(context.Server);
                }

                return ValueTask.FromResult(new EmptyResult());
            };
        });
    }
}

/// <summary>Tools подій FakeServer.</summary>
[McpServerToolType]
public sealed class FakeEventTools(FakeEvents events)
{
    /// <summary>Додає подію і сповіщає підписників ресурсу <c>fake://events</c>.</summary>
    [McpServerTool(Name = "emit_event", Title = "Створити подію", UseStructuredContent = true)]
    [Description("Додає подію з id і payload до fake://events і сповіщає підписників.")]
    public async Task<EmittedEvent> EmitEvent(
        [Description("Ключ події")] string id,
        [Description("Дані події")] JsonElement? payload = null,
        CancellationToken ct = default)
    {
        var node = payload is { ValueKind: not JsonValueKind.Undefined } element ? JsonNode.Parse(element.GetRawText()) : null;
        var notified = await events.EmitAsync(id, node, ct);
        return new(id, notified);
    }

    /// <summary>Останні події — для перевірки без підписки.</summary>
    [McpServerTool(Name = "list_events", Title = "Події", ReadOnly = true, UseStructuredContent = true)]
    [Description("Повертає останні події як { result: [...] }.")]
    public JsonObject ListEvents() => (JsonObject)JsonNode.Parse(events.ReadJson())!;
}

/// <summary>Результат <c>emit_event</c>.</summary>
public sealed record EmittedEvent(string Id, int Notified);

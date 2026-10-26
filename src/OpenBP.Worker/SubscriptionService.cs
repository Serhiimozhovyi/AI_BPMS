using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Npgsql;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker;

/// <summary>Підсумок одного проходу <see cref="SubscriptionService.ScanAsync"/> (для журналу й тестів).</summary>
/// <param name="Desired">Скільки вузлів <c>mcp.subscribe</c> в активних сценаріях.</param>
/// <param name="Held">Скільки підписок тримає цей воркер після проходу.</param>
/// <param name="Opened">Скільки підписок відкрито цим проходом.</param>
/// <param name="Closed">Скільки підписок закрито цим проходом.</param>
public sealed record SubscriptionScan(int Desired, int Held, int Opened, int Closed);

/// <summary>
/// Тримає підписки push-тригерів <c>mcp.subscribe</c> (F6-13, docs/connectors.md §9). Раз на
/// <see cref="WorkerOptions.SubscriptionScanInterval"/>:
/// <list type="number">
/// <item>збирає вузли <c>mcp.subscribe</c> опублікованих версій активних сценаріїв і звіряє з ними таблицю
/// <c>subscriptions</c> (рядок на вузол; зайві рядки видаляються, нові додаються);</item>
/// <item>для кожного рядка намагається взяти або продовжити оренду (<c>worker_id</c>, <c>leased_until</c> =
/// now + <see cref="WorkerOptions.SubscriptionLease"/>): оренду бере воркер, у якого вона вже є, або будь-який, коли вона
/// вільна чи протухла — так підписку впалого воркера за строк оренди підхоплює інший;</item>
/// <item>для орендованих відкриває окрему сесію з конектором (<see cref="IMcpServerRegistry.OpenSessionAsync"/>,
/// протокол <see cref="McpSubscriptions.ProtocolVersion"/>) і <c>resources/subscribe</c> на <c>resourceUri</c>; одразу
/// після відкриття — читання «надолужити» (події, що сталися, поки підписки не було).</item>
/// </list>
/// Сповіщення <c>notifications/resources/updated</c> не несе даних: воркер виконує вузол через
/// <see cref="PollJobProcessor.PollAsync"/> — той самий шлях, що й <c>mcp.poll</c>: читання подій, дедуплікація за
/// <c>keyField</c> у <c>node_state</c>, виконання і стан однією транзакцією. Тому повторне чи подвоєне сповіщення
/// (два воркери на мить тримають одну підписку) нового виконання не дає. Сповіщення під час читання не губиться:
/// після читання воркер читає ще раз. Обірвана сесія закривається, наступний прохід відкриває її знову (з наростаючою
/// паузою після невдач); зупинка воркера закриває сесії і звільняє оренди одразу.
/// </summary>
public sealed partial class SubscriptionService : BackgroundService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<OpenBpDbContext> _contexts;
    private readonly IMcpServerRegistry _registry;
    private readonly McpServerSync _servers;
    private readonly PollJobProcessor _poll;
    private readonly WorkerOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<SubscriptionKey, Held> _held = [];
    private readonly Dictionary<SubscriptionKey, (int Attempts, DateTimeOffset NextTry)> _failures = [];
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Створює сервіс.</summary>
    /// <param name="contexts">Фабрика контекстів БД.</param>
    /// <param name="registry">Реєстр MCP-серверів воркера.</param>
    /// <param name="servers">Синхронізація реєстру з <c>mcp_servers</c>.</param>
    /// <param name="poll">Обробник опитувань: виконує вузол і ставить виконання.</param>
    /// <param name="options">Налаштування воркера.</param>
    /// <param name="logger">Системний лог.</param>
    /// <param name="time">Годинник; за замовчуванням системний.</param>
    public SubscriptionService(
        IDbContextFactory<OpenBpDbContext> contexts,
        IMcpServerRegistry registry,
        McpServerSync servers,
        PollJobProcessor poll,
        WorkerOptions options,
        ILogger<SubscriptionService> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
        _poll = poll ?? throw new ArgumentNullException(nameof(poll));
        _options = options;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Підписки, які зараз тримає цей воркер (сесія відкрита).</summary>
    public IReadOnlyCollection<(Guid WorkflowId, string NodeId)> HeldSubscriptions
    {
        get
        {
            lock (_held)
            {
                return [.. _held.Keys.Select(k => (k.WorkflowId, k.NodeId))];
            }
        }
    }

    /// <summary>Один прохід: звірка з активними сценаріями, оренди, відкриття й закриття підписок.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<SubscriptionScan> ScanAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var desired = await LoadDesiredAsync(ct).ConfigureAwait(false);
            await ReconcileRowsAsync(desired, ct).ConfigureAwait(false);

            var opened = 0;
            var closed = 0;
            var now = _time.GetUtcNow();
            foreach (var (key, target) in desired)
            {
                var leased = await TryLeaseAsync(key, now, ct).ConfigureAwait(false);
                var held = Get(key);
                if (!leased)
                {
                    if (held is not null)
                    {
                        await CloseAsync(key, held, "оренду взяв інший воркер").ConfigureAwait(false);
                        closed++;
                    }

                    continue;
                }

                if (held is not null && (held.IsClosed || held.Target.ServerId != target.ServerId || held.Target.ResourceUri != target.ResourceUri))
                {
                    await CloseAsync(key, held, held.IsClosed ? "сесія обірвалася" : "параметри вузла змінилися").ConfigureAwait(false);
                    closed++;
                    held = null;
                }

                if (held is null && ShouldTry(key, now) && await OpenAsync(key, target, ct).ConfigureAwait(false))
                {
                    opened++;
                }
            }

            foreach (var (key, held) in Snapshot())
            {
                if (!desired.ContainsKey(key))
                {
                    await CloseAsync(key, held, "сценарій неактивний або вузла більше немає").ConfigureAwait(false);
                    closed++;
                }
            }

            int count;
            lock (_held)
            {
                count = _held.Count;
            }

            return new(desired.Count, count, opened, closed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Закриває всі сесії й звільняє оренди цього воркера (штатна зупинка).</summary>
    /// <param name="ct">Скасування.</param>
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (key, held) in Snapshot())
            {
                await CloseAsync(key, held, "воркер зупиняється").ConfigureAwait(false);
            }

            await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Subscriptions.Where(s => s.WorkerId == _options.WorkerId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.WorkerId, (string?)null).SetProperty(s => s.LeasedUntil, (DateTimeOffset?)null), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// «Падіння воркера» для тестів: сесії закриваються, а оренди лишаються — їх мають підхопити інші воркери після
    /// строку оренди.
    /// </summary>
    public async Task DropAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (key, held) in Snapshot())
            {
                await CloseAsync(key, held, "імітація падіння").ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var registration = stoppingToken.Register(() => _stopping.Cancel());
        using var timer = new PeriodicTimer(_options.SubscriptionScanInterval, _time);
        try
        {
            do
            {
                try
                {
                    var scan = await ScanAsync(stoppingToken).ConfigureAwait(false);
                    if (scan.Opened > 0 || scan.Closed > 0)
                    {
                        LogScan(_logger, scan.Desired, scan.Held, scan.Opened, scan.Closed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    LogScanFailed(_logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        try
        {
            await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or DbUpdateException or InvalidOperationException)
        {
            // Оренди протухнуть самі; інший воркер підхопить підписки за строк оренди.
            LogReleaseFailed(_logger, ex);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _stopping.Dispose();
        _gate.Dispose();
        base.Dispose();
    }

    // ---------- активні сценарії → бажані підписки ----------

    private async Task<Dictionary<SubscriptionKey, Target>> LoadDesiredAsync(CancellationToken ct)
    {
        List<(Guid WorkflowId, string Definition)> rows;
        await using (var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            rows = (await db.Workflows.AsNoTracking()
                .Where(w => w.Active)
                .Join(db.WorkflowVersions, w => w.PublishedVersionId ?? w.CurrentVersionId, v => v.Id, (w, v) => new { w.Id, v.Definition })
                .ToListAsync(ct)
                .ConfigureAwait(false))
                .Select(r => (r.Id, r.Definition))
                .ToList();
        }

        var desired = new Dictionary<SubscriptionKey, Target>();
        foreach (var (workflowId, definition) in rows)
        {
            WorkflowDefinition workflow;
            try
            {
                workflow = WorkflowJson.Deserialize(definition);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            {
                LogBadDefinition(_logger, workflowId, ex.Message);
                continue;
            }

            foreach (var node in workflow.Nodes)
            {
                if (node.Type != McpNodeSchemaProvider.SubscribeNodeType || node.Disabled == true)
                {
                    continue;
                }

                var serverId = Text(node.Parameters["serverId"]);
                var uri = Text(node.Parameters["resourceUri"]);
                if (serverId is null || uri is null)
                {
                    // Конектор чи ресурс — вираз або порожнє: статично підписатися нема на що (валідатор це теж помічає).
                    continue;
                }

                var single = new WorkflowDefinition
                {
                    FormatVersion = workflow.FormatVersion,
                    Name = workflow.Name,
                    Settings = workflow.Settings,
                    Nodes = [node],
                };
                desired[new(workflowId, node.Id)] = new(serverId, uri, single);
            }
        }

        return desired;

        static string? Text(JsonNode? value) =>
            value is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && !ExpressionSyntax.ContainsExpression(text)
                ? text
                : null;
    }

    private async Task ReconcileRowsAsync(Dictionary<SubscriptionKey, Target> desired, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Subscriptions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        foreach (var row in rows)
        {
            var key = new SubscriptionKey(row.WorkflowId, row.NodeId);
            if (!desired.TryGetValue(key, out var target))
            {
                await db.Subscriptions.Where(s => s.WorkflowId == key.WorkflowId && s.NodeId == key.NodeId)
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
            else if (row.ServerId != target.ServerId || row.ResourceUri != target.ResourceUri)
            {
                await db.Subscriptions.Where(s => s.WorkflowId == key.WorkflowId && s.NodeId == key.NodeId)
                    .ExecuteUpdateAsync(
                        u => u.SetProperty(s => s.ServerId, target.ServerId).SetProperty(s => s.ResourceUri, target.ResourceUri).SetProperty(s => s.UpdatedAt, now),
                        ct)
                    .ConfigureAwait(false);
            }
        }

        var known = rows.Select(r => new SubscriptionKey(r.WorkflowId, r.NodeId)).ToHashSet();
        foreach (var (key, target) in desired)
        {
            if (known.Contains(key))
            {
                continue;
            }

            db.Subscriptions.Add(new SubscriptionEntity
            {
                WorkflowId = key.WorkflowId,
                NodeId = key.NodeId,
                ServerId = target.ServerId,
                ResourceUri = target.ResourceUri,
                UpdatedAt = now,
            });
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation })
            {
                // Рядок щойно додав інший воркер або сценарій щойно видалено.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<bool> TryLeaseAsync(SubscriptionKey key, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var until = now + _options.SubscriptionLease;
        var me = _options.WorkerId;
        var updated = await db.Subscriptions
            .Where(s => s.WorkflowId == key.WorkflowId && s.NodeId == key.NodeId
                && (s.WorkerId == me || s.LeasedUntil == null || s.LeasedUntil < now))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.WorkerId, me).SetProperty(s => s.LeasedUntil, until).SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return updated == 1;
    }

    // ---------- сесії ----------

    private async Task<bool> OpenAsync(SubscriptionKey key, Target target, CancellationToken ct)
    {
        McpClient? client = null;
        try
        {
            await _servers.SyncAsync(target.Workflow, new SecretScrubber(), ct).ConfigureAwait(false);
            client = await _registry.OpenSessionAsync(target.ServerId, McpSubscriptions.ProtocolVersion, ct).ConfigureAwait(false);
            var held = new Held(target, client);
            var subscription = await client.SubscribeToResourceAsync(
                target.ResourceUri,
                (_, _) =>
                {
                    _ = DeliverAsync(key, held);
                    return ValueTask.CompletedTask;
                },
                cancellationToken: ct).ConfigureAwait(false);
            held.Subscription = subscription;
            lock (_held)
            {
                _held[key] = held;
            }

            _failures.Remove(key);
            LogOpened(_logger, key.WorkflowId, key.NodeId, target.ResourceUri);

            // Надолужити: події, що сталися без підписки (рестарт, переїзд на інший воркер).
            await DeliverAsync(key, held).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (client is not null)
            {
                await DisposeQuietlyAsync(client).ConfigureAwait(false);
            }

            var attempts = _failures.TryGetValue(key, out var f) ? f.Attempts + 1 : 1;
            var delay = TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, _options.SubscriptionScanInterval.Ticks * (1L << Math.Min(attempts, 10))));
            _failures[key] = (attempts, _time.GetUtcNow() + delay);
            LogOpenFailed(_logger, key.WorkflowId, key.NodeId, attempts, ex.Message);
            return false;
        }
    }

    private bool ShouldTry(SubscriptionKey key, DateTimeOffset now) =>
        !_failures.TryGetValue(key, out var failure) || failure.NextTry <= now;

    /// <summary>Виконує вузол через обробник опитувань; сповіщення під час читання — ще одне читання після.</summary>
    private async Task DeliverAsync(SubscriptionKey key, Held held)
    {
        Interlocked.Increment(ref held.Pending);
        if (!await held.Gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            while (true)
            {
                while (Interlocked.Exchange(ref held.Pending, 0) > 0)
                {
                    if (_stopping.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        var result = await _poll.PollAsync(new PollJobPayload { WorkflowId = key.WorkflowId, NodeId = key.NodeId }, _stopping.Token).ConfigureAwait(false);
                        LogDelivered(_logger, key.WorkflowId, key.NodeId, result.Outcome, result.ItemCount);
                    }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogDeliverFailed(_logger, ex, key.WorkflowId, key.NodeId);
                    }
                }

                held.Gate.Release();
                if (Volatile.Read(ref held.Pending) == 0 || !await held.Gate.WaitAsync(0).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch
        {
            held.Gate.Release();
            throw;
        }
    }

    private async Task CloseAsync(SubscriptionKey key, Held held, string reason)
    {
        lock (_held)
        {
            if (_held.TryGetValue(key, out var current) && ReferenceEquals(current, held))
            {
                _held.Remove(key);
            }
        }

        LogClosed(_logger, key.WorkflowId, key.NodeId, reason);
        if (held.Subscription is { } subscription && !held.IsClosed)
        {
            try
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Сесія вже мертва — важливо лише закрити клієнта.
            }
        }

        await DisposeQuietlyAsync(held.Client).ConfigureAwait(false);
    }

    private static async Task DisposeQuietlyAsync(McpClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Процес конектора не завершився вчасно або з'єднання вже закрите.
        }
    }

    private Held? Get(SubscriptionKey key)
    {
        lock (_held)
        {
            return _held.GetValueOrDefault(key);
        }
    }

    private List<KeyValuePair<SubscriptionKey, Held>> Snapshot()
    {
        lock (_held)
        {
            return [.. _held];
        }
    }

    private readonly record struct SubscriptionKey(Guid WorkflowId, string NodeId);

    private sealed record Target(string ServerId, string ResourceUri, WorkflowDefinition Workflow);

    private sealed class Held(Target target, McpClient client)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Pending;

        public Target Target { get; } = target;

        public McpClient Client { get; } = client;

        public IAsyncDisposable? Subscription { get; set; }

        public bool IsClosed => Client.Completion.IsCompleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Підписки: активних вузлів {Desired}, тримає цей воркер {Held}, відкрито {Opened}, закрито {Closed}.")]
    private static partial void LogScan(ILogger logger, int desired, int held, int opened, int closed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Підписки: прохід не вдався; повтор за розкладом.")]
    private static partial void LogScanFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Підписки: не вдалося звільнити оренди при зупинці.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Підписки: визначення сценарію {WorkflowId} не читається ({Error}); пропущено.")]
    private static partial void LogBadDefinition(ILogger logger, Guid workflowId, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Підписка {WorkflowId}/{NodeId} на {ResourceUri} відкрита.")]
    private static partial void LogOpened(ILogger logger, Guid workflowId, string nodeId, string resourceUri);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Підписка {WorkflowId}/{NodeId}: не вдалося відкрити (спроба {Attempts}): {Error}.")]
    private static partial void LogOpenFailed(ILogger logger, Guid workflowId, string nodeId, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Підписка {WorkflowId}/{NodeId} закрита: {Reason}.")]
    private static partial void LogClosed(ILogger logger, Guid workflowId, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Підписка {WorkflowId}/{NodeId}: подія → {Outcome}, нових записів {Count}.")]
    private static partial void LogDelivered(ILogger logger, Guid workflowId, string nodeId, PollOutcome outcome, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Підписка {WorkflowId}/{NodeId}: не вдалося обробити подію.")]
    private static partial void LogDeliverFailed(ILogger logger, Exception exception, Guid workflowId, string nodeId);
}

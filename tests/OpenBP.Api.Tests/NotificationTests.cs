using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Auth;
using OpenBP.Application.Executions;
using OpenBP.Application.Notifications;
using OpenBP.Application.Overview;
using OpenBP.Data;

namespace OpenBP.Api.Tests;

/// <summary>F6-07: канали сповіщень, розсилка з cooldown, перевірки стану, «Огляд», агрегати і <c>/metrics</c>.</summary>
[Collection(ApiCollection.Name)]
public class NotificationTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Channels = "/api/v1/notification-channels";

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private static string DotNet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    private static string FakeServerDll => Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private static async Task<Guid> CreateWorkflowAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/workflows", JsonNode.Parse($$"""
            {
              "formatVersion": 1,
              "name": {{JsonSerializer.Serialize(name)}},
              "nodes": [ { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} } ],
              "connections": []
            }
            """));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Guid.Parse((await JsonAsync(response)).GetProperty("id").GetString()!);
    }

    private Task<int> DispatchAsync() => InScopeAsync(sp => sp.GetRequiredService<NotificationDispatcher>().DispatchAsync(default));

    private async Task NotifyAsync(params (string Event, string Subject, DateTimeOffset At)[] events)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenBpDbContext>();
        foreach (var (eventName, subject, at) in events)
        {
            db.Notify(at, eventName, subject, $"Подія {subject}", new JsonObject { ["workflow"] = subject });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Приймач webhook-ів: тіла й заголовок <c>X-Token</c>.</summary>
    private static async Task<(WebApplication App, ConcurrentQueue<(JsonObject Body, string? Token)> Received)> WebhookReceiverAsync()
    {
        var received = new ConcurrentQueue<(JsonObject, string?)>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/hook", async (HttpRequest request) =>
        {
            var body = (await JsonNode.ParseAsync(request.Body))!.AsObject();
            received.Enqueue((body, request.Headers["X-Token"].FirstOrDefault()));
            return Results.Ok();
        });
        await app.StartAsync();
        return (app, received);
    }

    [Fact]
    public async Task Channels_are_admin_only_and_validated()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var editor = await LoginAsync(UserRole.Editor);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync(Channels)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync(Channels)).StatusCode);

        var bad = new object[]
        {
            new { name = "x", type = "sms", config = new { }, events = new[] { "execution.failed" } },
            new { name = "x", type = "webhook", config = new { url = "ftp://x" }, events = new[] { "execution.failed" } },
            new { name = "x", type = "webhook", config = new { url = "https://example.com/hook" }, events = new[] { "nope" } },
            new { name = "x", type = "telegram", config = new { connectorId = Guid.NewGuid(), chatId = "1" }, events = new[] { "execution.failed" } },
            new { name = "x", type = "email", config = new { connectorId = Guid.NewGuid(), from = "a@b", to = Array.Empty<string>() }, events = new[] { "execution.failed" } },
            new { name = "x", type = "webhook", config = new { url = "https://example.com/hook" }, events = new[] { "execution.failed" }, cooldownSec = -1 },
        };
        foreach (var body in bad)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Channels, body)).StatusCode);
        }

        var name = Unique("Хук");
        var created = await admin.PostAsJsonAsync(Channels, new
        {
            name,
            type = "webhook",
            config = new { url = "https://example.com/hook", extra = "відкидається" },
            events = new[] { "execution.failed", "worker.stale" },
            cooldownSec = 60,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var channel = await JsonAsync(created);
        var id = channel.GetProperty("id").GetGuid();
        Assert.False(channel.GetProperty("config").TryGetProperty("extra", out _));
        Assert.True(channel.GetProperty("enabled").GetBoolean());

        var updated = await JsonAsync(await admin.PutAsJsonAsync($"{Channels}/{id}", new
        {
            name,
            type = "webhook",
            config = new { url = "https://example.com/other" },
            events = new[] { "backup.failed" },
            cooldownSec = 0,
            enabled = false,
        }));
        Assert.Equal(["backup.failed"], updated.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
        Assert.False(updated.GetProperty("enabled").GetBoolean());
        Assert.Contains((await JsonAsync(await admin.GetAsync(Channels))).EnumerateArray(), c => c.GetProperty("id").GetGuid() == id);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Channels}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"{Channels}/{id}")).StatusCode);
    }

    [Fact]
    public async Task Webhook_channel_gets_subscribed_events_once_per_cooldown_with_credential_headers()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var (receiver, received) = await WebhookReceiverAsync();
        await using var _ = receiver;
        var credential = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/credentials", new
        {
            name = Unique("Хук"),
            type = "mcp.http",
            data = new { headers = new Dictionary<string, string> { ["X-Token"] = "hook-secret-1" } },
        }));
        var channel = await JsonAsync(await admin.PostAsJsonAsync(Channels, new
        {
            name = Unique("Хук"),
            type = "webhook",
            config = new { url = receiver.Urls.First() + "/hook", credentialId = credential.GetProperty("id").GetString() },
            events = new[] { "execution.failed", "workflow.disabled" },
            cooldownSec = 600,
        }));
        var channelId = channel.GetProperty("id").GetGuid();

        var a = Unique("a");
        var b = Unique("b");
        var now = DateTimeOffset.UtcNow;
        await NotifyAsync(
            (NotificationEvents.ExecutionFailed, a, now.AddSeconds(-3)),
            (NotificationEvents.ExecutionFailed, a, now.AddSeconds(-2)),
            (NotificationEvents.WorkflowDisabled, a, now.AddSeconds(-2)),
            (NotificationEvents.ExecutionFailed, b, now.AddSeconds(-1)),
            (NotificationEvents.WorkerStale, a, now));
        await DispatchAsync();

        var mine = received.Where(r => r.Body["subject"]?.GetValue<string>() is { } s && (s == a || s == b)).ToList();
        Assert.Equal(
            [$"{NotificationEvents.ExecutionFailed}:{a}", $"{NotificationEvents.WorkflowDisabled}:{a}", $"{NotificationEvents.ExecutionFailed}:{b}"],
            mine.Select(r => $"{r.Body["event"]}:{r.Body["subject"]}"));
        Assert.All(mine, r => Assert.Equal("hook-secret-1", r.Token));
        Assert.Equal(a, mine[0].Body["details"]!["workflow"]!.GetValue<string>());

        // Після cooldown — знову; поки триває — ні.
        await NotifyAsync((NotificationEvents.ExecutionFailed, a, now.AddSeconds(1)));
        await DispatchAsync();
        Assert.Equal(3, received.Count(r => r.Body["subject"]?.GetValue<string>() is { } s && (s == a || s == b)));
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OpenBpDbContext>();
            await db.NotificationSends.Where(s => s.ChannelId == channelId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SentAt, now.AddHours(-1)));
            return 0;
        });
        await NotifyAsync((NotificationEvents.ExecutionFailed, a, DateTimeOffset.UtcNow));
        await DispatchAsync();
        Assert.Equal(4, received.Count(r => r.Body["subject"]?.GetValue<string>() is { } s && (s == a || s == b)));
        Assert.Equal(0, await InScopeAsync(sp => sp.GetRequiredService<OpenBpDbContext>().Notifications.CountAsync(n => n.ProcessedAt == null && (n.Subject == a || n.Subject == b))));

        // Тест каналу — одразу, без cooldown.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Channels}/{channelId}/test", null)).StatusCode);
        Assert.Contains(received, r => r.Body["event"]?.GetValue<string>() == "test");
        await admin.DeleteAsync($"{Channels}/{channelId}");
    }

    [Fact]
    public async Task Telegram_and_email_channels_call_connector_tools()
    {
        var admin = await LoginAsync(UserRole.Admin);
        async Task<string> ConnectorAsync(string mock)
        {
            var response = await admin.PostAsJsonAsync("/api/v1/connectors", new
            {
                name = Unique(mock),
                transport = "stdio",
                config = new { command = DotNet, arguments = new[] { FakeServerDll, "--mock", mock } },
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await JsonAsync(response)).GetProperty("summary").GetProperty("id").GetString()!;
        }

        var telegram = await JsonAsync(await admin.PostAsJsonAsync(Channels, new
        {
            name = Unique("Telegram"),
            type = "telegram",
            config = new { connectorId = await ConnectorAsync("telegram"), chatId = "4242" },
            events = new[] { "connector.unavailable" },
        }));
        var email = await JsonAsync(await admin.PostAsJsonAsync(Channels, new
        {
            name = Unique("Пошта"),
            type = "email",
            config = new { connectorId = await ConnectorAsync("msgraph"), from = "ops@example.com", to = new[] { "admin@example.com" } },
            events = new[] { "connector.unavailable" },
        }));

        var subject = Unique("crm");
        await NotifyAsync((NotificationEvents.ConnectorUnavailable, subject, DateTimeOffset.UtcNow));
        await DispatchAsync();

        foreach (var channel in new[] { telegram, email })
        {
            var id = channel.GetProperty("id").GetGuid();
            Assert.True(await InScopeAsync(sp => sp.GetRequiredService<OpenBpDbContext>().NotificationSends
                .AnyAsync(s => s.ChannelId == id && s.Key == $"{NotificationEvents.ConnectorUnavailable}:{subject}")), $"Канал {channel.GetProperty("type")} не надіслав повідомлення.");
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"{Channels}/{id}/test", null)).StatusCode);
            await admin.DeleteAsync($"{Channels}/{id}");
        }
    }

    [Fact]
    public async Task Unreachable_connector_discovery_adds_one_event_per_failure_streak()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var created = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/connectors", new
        {
            name = Unique("Недоступний"),
            transport = "http",
            config = new { url = "http://127.0.0.1:1/mcp" },
        }));
        var id = created.GetProperty("summary").GetProperty("id").GetString()!;
        await admin.PostAsync($"/api/v1/connectors/{id}/refresh-tools", null);
        await admin.PostAsync($"/api/v1/connectors/{id}/refresh-tools", null);

        var events = await InScopeAsync(sp => sp.GetRequiredService<OpenBpDbContext>().Notifications.AsNoTracking()
            .Where(n => n.Event == NotificationEvents.ConnectorUnavailable && n.Subject == id).ToListAsync());
        var single = Assert.Single(events);
        Assert.Contains("Недоступний", single.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watchdog_reports_stale_worker_and_failed_backup_once()
    {
        var worker = Unique("worker");
        var now = DateTimeOffset.UtcNow;
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OpenBpDbContext>();
            db.WorkerHeartbeats.Add(new WorkerHeartbeatEntity { WorkerId = worker, StartedAt = now.AddHours(-1), SeenAt = now.AddMinutes(-5) });
            db.BackupRuns.Add(new BackupRunEntity { At = now.AddMinutes(-1), Ok = false, Message = "pg_dump завершився помилкою" });
            return await db.SaveChangesAsync();
        });

        Assert.True(await InScopeAsync(sp => sp.GetRequiredService<OpsWatchdog>().CheckAsync(default)) >= 2);
        Assert.Equal(0, await InScopeAsync(sp => sp.GetRequiredService<OpsWatchdog>().CheckAsync(default)));
        var events = await InScopeAsync(sp => sp.GetRequiredService<OpenBpDbContext>().Notifications.AsNoTracking()
            .Where(n => (n.Event == NotificationEvents.WorkerStale && n.Subject == worker) || n.Event == NotificationEvents.BackupFailed)
            .Where(n => n.CreatedAt >= now.AddSeconds(-5)).ToListAsync());
        Assert.Single(events, e => e.Event == NotificationEvents.WorkerStale);
        var backup = Assert.Single(events, e => e.Event == NotificationEvents.BackupFailed);
        Assert.Contains("pg_dump", backup.Details, StringComparison.Ordinal);

        // Новий успішний запуск закриває епізод: подій більше немає.
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OpenBpDbContext>();
            db.BackupRuns.Add(new BackupRunEntity { At = DateTimeOffset.UtcNow, Ok = true, Message = "ok" });
            await db.WorkerHeartbeats.Where(w => w.WorkerId == worker).ExecuteDeleteAsync();
            return await db.SaveChangesAsync();
        });
        Assert.Equal(0, await InScopeAsync(sp => sp.GetRequiredService<OpsWatchdog>().CheckAsync(default)));
    }

    [Fact]
    public async Task Stats_match_executions_and_feed_overview_and_metrics()
    {
        var viewer = await LoginAsync(UserRole.Viewer);
        var admin = await LoginAsync(UserRole.Admin);
        var now = DateTimeOffset.UtcNow;
        var noisyName = Unique("Шумний \"сценарій\"");
        var noisy = await CreateWorkflowAsync(admin, noisyName);
        var quiet = await CreateWorkflowAsync(admin, Unique("Тихий"));
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OpenBpDbContext>();
            void Add(Guid workflow, string status, int minutesAgo, int? durationMs)
            {
                var finished = now.AddMinutes(-minutesAgo);
                db.Executions.Add(new ExecutionEntity
                {
                    Id = Guid.NewGuid(),
                    WorkflowId = workflow,
                    VersionId = Guid.NewGuid(),
                    Mode = "trigger",
                    Status = status,
                    StartedAt = durationMs is { } ms ? finished.AddMilliseconds(-ms) : null,
                    FinishedAt = finished,
                });
            }

            for (var i = 0; i < 60; i++)
            {
                Add(noisy, i % 3 == 0 ? "success" : i % 3 == 1 ? "error" : "timed_out", 1 + (i % 50), 80 + (i * 37 % 2000));
            }

            Add(quiet, "success", 5, 200);
            Add(quiet, "cancelled", 6, null);
            Add(quiet, "running", 0, null);
            return await db.SaveChangesAsync();
        });

        await InScopeAsync(sp => sp.GetRequiredService<ExecutionStatsAggregator>().RefreshAsync(default));

        // Агрегати збігаються з executions.
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OpenBpDbContext>();
            foreach (var id in new[] { noisy, quiet })
            {
                var rows = await db.ExecutionStats.AsNoTracking().Where(s => s.WorkflowId == id).ToListAsync();
                var executions = await db.Executions.AsNoTracking()
                    .Where(e => e.WorkflowId == id && e.FinishedAt != null && e.Status != "running").ToListAsync();
                Assert.Equal(executions.Count, rows.Sum(r => r.Total));
                Assert.Equal(executions.Count(e => e.Status == "success"), rows.Sum(r => r.Success));
                Assert.Equal(executions.Count(e => e.Status == "error"), rows.Sum(r => r.Error));
                Assert.Equal(executions.Count(e => e.Status == "timed_out"), rows.Sum(r => r.TimedOut));
                Assert.Equal(executions.Count(e => e.Status == "cancelled"), rows.Sum(r => r.Cancelled));
                var durations = executions.Where(e => e.StartedAt != null).Select(e => (long)(e.FinishedAt!.Value - e.StartedAt!.Value).TotalMilliseconds).ToList();
                Assert.Equal(durations.Count, rows.Sum(r => r.DurationBuckets.Sum()));
                Assert.InRange(rows.Sum(r => r.DurationSumMs), durations.Sum() - durations.Count, durations.Sum() + durations.Count);
                foreach (var row in rows)
                {
                    Assert.Equal(new DateTimeOffset(row.Hour.UtcDateTime.Date.AddHours(row.Hour.UtcDateTime.Hour), TimeSpan.Zero), row.Hour);
                }
            }

            return 0;
        });

        // Повторний перерахунок нічого не подвоює.
        await InScopeAsync(sp => sp.GetRequiredService<ExecutionStatsAggregator>().RefreshAsync(default));
        Assert.Equal(60, await InScopeAsync(sp => sp.GetRequiredService<OpenBpDbContext>().ExecutionStats.Where(s => s.WorkflowId == noisy).SumAsync(s => s.Total)));

        var overview = await JsonAsync(await viewer.GetAsync("/api/v1/overview?range=24h"));
        Assert.True(overview.GetProperty("total").GetInt32() >= 62);
        var top = overview.GetProperty("topFailing").EnumerateArray().First(w => w.GetProperty("workflowId").GetGuid() == noisy);
        Assert.Equal(40, top.GetProperty("failed").GetInt32());
        Assert.Equal(noisyName, top.GetProperty("name").GetString());
        Assert.True(overview.GetProperty("running").GetInt32() >= 1);
        Assert.True(overview.GetProperty("p95Ms").GetInt64() >= overview.GetProperty("p50Ms").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/v1/overview?range=1y")).StatusCode);

        // /metrics: лише admin (сесія чи API-ключ), без секретів.
        const string secret = "metrics-secret-value-77";
        await admin.PostAsJsonAsync("/api/v1/credentials", new { name = Unique("Секрет"), type = "generic", data = new { token = secret } });
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/metrics")).StatusCode);
        var response = await admin.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains($"openbp_executions_total{{status=\"error\",workflow_id=\"{noisy}\",workflow=\"{noisyName.Replace("\"", "\\\"", StringComparison.Ordinal)}\"}} 20", text, StringComparison.Ordinal);
        Assert.Contains("openbp_execution_duration_seconds_bucket{le=\"+Inf\"}", text, StringComparison.Ordinal);
        Assert.Contains("openbp_queue_depth{kind=\"execution\"}", text, StringComparison.Ordinal);
        Assert.Contains("openbp_queue_oldest_job_age_seconds", text, StringComparison.Ordinal);
        Assert.Contains("openbp_active_subscriptions{kind=\"webhook\"}", text, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);

        var key = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/api-keys", new { name = "prometheus" }));
        using var byKey = api.CreateClient();
        byKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.GetProperty("key").GetString());
        Assert.Equal(HttpStatusCode.OK, (await byKey.GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task Retention_purges_only_old_processed_notifications()
    {
        var tag = Unique("purge");
        var now = DateTimeOffset.UtcNow;
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OpenBpDbContext>();
            foreach (var (name, age, processed) in new[] { ("old", 40, true), ("pending", 40, false), ("fresh", 1, true) })
            {
                db.Notifications.Add(new NotificationEntity
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = now.AddDays(-age),
                    Event = NotificationEvents.ExecutionFailed,
                    Subject = $"{tag}-{name}",
                    Title = name,
                    Details = "{}",
                    ProcessedAt = processed ? now.AddDays(-age) : null,
                });
            }

            return await db.SaveChangesAsync();
        });

        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<OpenBpDbContext>();
            Assert.Equal(0, await new ExecutionRetentionService(db, new ExecutionRetentionOptions { Days = 0 }, TimeProvider.System).PurgeNotificationsAsync(default));
            return await new ExecutionRetentionService(db, new ExecutionRetentionOptions { Days = 30 }, TimeProvider.System).PurgeNotificationsAsync(default);
        });

        var left = await InScopeAsync(sp => sp.GetRequiredService<OpenBpDbContext>().Notifications.AsNoTracking()
            .Where(n => n.Subject.StartsWith(tag)).Select(n => n.Title).ToListAsync());
        Assert.Equal(["fresh", "pending"], left.Order());
    }

    [Fact]
    public void Percentile_is_upper_bound_of_bucket()
    {
        long[] buckets = new long[ExecutionStatsBuckets.Count];
        Assert.Null(ExecutionStatsBuckets.Percentile(buckets, 0.5));
        buckets[0] = 50;
        buckets[3] = 45;
        buckets[^1] = 5;
        Assert.Equal(100, ExecutionStatsBuckets.Percentile(buckets, 0.5));
        Assert.Equal(1_000, ExecutionStatsBuckets.Percentile(buckets, 0.95));
        Assert.Equal(3_600_000, ExecutionStatsBuckets.Percentile(buckets, 0.99));
    }
}

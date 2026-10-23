using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Auth;
using OpenBP.Application.Jobs;
using OpenBP.Application.Schedules;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using Quartz;

namespace OpenBP.Api.Tests;

/// <summary>F2-11: тригер <c>core.schedule</c> — Quartz-розклад при активації, спрацювання ставить виконання.</summary>
[Collection(ApiCollection.Name)]
public class ScheduleTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Base = "/api/v1/workflows";

    // ---------- спрацювання ----------

    [Fact]
    public async Task Schedule_with_fake_time_enqueues_execution_job_with_fire_item()
    {
        // Окремий хост зі своїм годинником у 2030 році: Quartz-вузол основного хоста (реальний час) цих тригерів
        // ще не бачить, а тригери інших тестів цей вузол вважає пропущеними і не запускає (DoNothing).
        var clock = new ManualClock(new DateTimeOffset(2030, 1, 1, 4, 59, 58, TimeSpan.Zero));
        await using var factory = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        var editor = await LoginAsync(factory);
        var id = await CreateAsync(editor, ScheduleNode("0 7 * * *"));
        await ActivateAsync(editor, id);

        var trigger = await TriggerAsync(factory, id);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 5, 0, 0, TimeSpan.Zero), trigger!.NextFireTimeUtc);

        clock.Now = new DateTimeOffset(2030, 1, 1, 5, 0, 1, TimeSpan.Zero);
        var execution = await WaitForExecutionAsync(id, TimeSpan.FromSeconds(30));

        Assert.Equal(ExecutionStatuses.Queued, execution.Status);
        Assert.Equal("trigger", execution.Mode);
        await using var db = api.CreateDbContext();
        var payload = await PayloadAsync(db, execution.Id);
        Assert.Equal(ExecutionMode.Trigger, payload.Mode);
        Assert.Equal("n_cron", payload.StartNodeId);
        var item = Assert.Single(payload.StartItems!).Json;
        Assert.Equal("2030-01-01T07:00:00+02:00", item["timestamp"]!.GetValue<string>());
        Assert.Equal("Europe/Kyiv", item["timezone"]!.GetValue<string>());
        Assert.Equal(1, await db.Executions.CountAsync(e => e.WorkflowId == id));

        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{id}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task Fire_skips_inactive_workflow_or_missing_node_and_reports_deleted_one()
    {
        var editor = await LoginAsync(api);
        var id = await CreateAsync(editor, ScheduleNode("0 7 * * *"));
        await using var scope = api.Services.CreateAsyncScope();
        var fire = scope.ServiceProvider.GetRequiredService<ScheduleFireService>();
        var at = DateTimeOffset.UtcNow;

        Assert.Equal(ScheduleFireStatus.Skipped, (await fire.FireAsync(id, "n_cron", at, CancellationToken.None)).Status);

        await ActivateAsync(editor, id);
        Assert.Equal(ScheduleFireStatus.Skipped, (await fire.FireAsync(id, "n_other", at, CancellationToken.None)).Status);
        Assert.Equal(ScheduleFireStatus.WorkflowNotFound, (await fire.FireAsync(Guid.NewGuid(), "n_cron", at, CancellationToken.None)).Status);
        await using (var db = api.CreateDbContext())
        {
            Assert.False(await db.Executions.AnyAsync(e => e.WorkflowId == id));
        }

        var launched = await fire.FireAsync(id, "n_cron", new DateTimeOffset(2026, 7, 1, 4, 0, 0, TimeSpan.Zero), CancellationToken.None);

        Assert.Equal(ScheduleFireStatus.Launched, launched.Status);
        await using var check = api.CreateDbContext();
        var item = Assert.Single((await PayloadAsync(check, launched.ExecutionId!.Value)).StartItems!).Json;
        Assert.Equal("2026-07-01T07:00:00+03:00", item["timestamp"]!.GetValue<string>());
        await DeactivateAsync(editor, id);
    }

    // ---------- реєстрація ----------

    [Fact]
    public async Task Activation_registers_cron_trigger_and_deactivation_removes_it()
    {
        var editor = await LoginAsync(api);
        var id = await CreateAsync(editor, ScheduleNode("0 7 * * 1-5", timezone: "America/New_York"));
        Assert.Null(await TriggerAsync(api, id));

        await ActivateAsync(editor, id);

        var trigger = Assert.IsAssignableFrom<ICronTrigger>(await TriggerAsync(api, id));
        Assert.Equal("0 0 7 ? * MON-FRI", trigger.CronExpressionString);
        Assert.Equal("America/New_York", trigger.TimeZone.Id);
        Assert.Equal(CronTriggerMisfireInstruction.DoNothing, (CronTriggerMisfireInstruction)trigger.MisfireInstructionCode);

        await DeactivateAsync(editor, id);

        var scheduler = await SchedulerAsync(api);
        Assert.False(await scheduler.Exists(QuartzScheduleRegistrar.JobKeyOf(id)));
        Assert.Null(await TriggerAsync(api, id));
    }

    [Fact]
    public async Task Saving_active_workflow_replaces_schedule_and_removing_node_drops_it()
    {
        var editor = await LoginAsync(api);
        var id = await CreateAsync(editor, ScheduleNode("0 7 * * *"));
        await ActivateAsync(editor, id);

        var saved = await editor.PutAsync($"{Base}/{id}", Json(ScheduleNode("0 0 9 ? * SAT")));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var trigger = Assert.IsAssignableFrom<ICronTrigger>(await TriggerAsync(api, id));
        Assert.Equal("0 0 9 ? * SAT", trigger.CronExpressionString);
        Assert.Equal("Europe/Kyiv", trigger.TimeZone.Id);

        var manual = Definition("""[{ "id": "n_manual", "name": "Ручний", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }]""");
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsync($"{Base}/{id}", Json(manual))).StatusCode);

        Assert.False(await (await SchedulerAsync(api)).Exists(QuartzScheduleRegistrar.JobKeyOf(id)));
        await DeactivateAsync(editor, id);
    }

    // ---------- mcp.poll (F2-12) ----------

    [Fact]
    public async Task Poll_node_gets_cron_trigger_on_activation_and_fire_enqueues_poll_job_not_execution()
    {
        var editor = await LoginAsync(api);
        var id = await CreateAsync(editor, PollNode("*/5 * * * *"));
        await ActivateAsync(editor, id);

        var trigger = Assert.IsAssignableFrom<ICronTrigger>(await (await SchedulerAsync(api)).GetTrigger(QuartzScheduleRegistrar.TriggerKeyOf(id, "n_poll")));
        Assert.Equal("0 */5 * * * ?", trigger.CronExpressionString);
        Assert.Equal("Europe/Kyiv", trigger.TimeZone.Id);

        await using var scope = api.Services.CreateAsyncScope();
        var fire = scope.ServiceProvider.GetRequiredService<ScheduleFireService>();
        var queued = await fire.FireAsync(id, "n_poll", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(ScheduleFireStatus.PollQueued, queued.Status);
        Assert.Null(queued.ExecutionId);
        await using (var db = api.CreateDbContext())
        {
            var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == queued.JobId);
            Assert.Equal(JobKinds.Poll, job.Kind);
            var payload = JobJson.Deserialize<PollJobPayload>(job.Payload);
            Assert.Equal(id, payload.WorkflowId);
            Assert.Equal("n_poll", payload.NodeId);
            Assert.False(await db.Executions.AnyAsync(e => e.WorkflowId == id));
        }

        // Попередній job ще чекає воркера — другий не ставиться.
        Assert.Equal(ScheduleFireStatus.Skipped, (await fire.FireAsync(id, "n_poll", DateTimeOffset.UtcNow, CancellationToken.None)).Status);

        await DeactivateAsync(editor, id);
        Assert.Null(await (await SchedulerAsync(api)).GetTrigger(QuartzScheduleRegistrar.TriggerKeyOf(id, "n_poll")));
        Assert.Equal(ScheduleFireStatus.Skipped, (await fire.FireAsync(id, "n_poll", DateTimeOffset.UtcNow, CancellationToken.None)).Status);
        await using var cleanup = api.CreateDbContext();
        await cleanup.Jobs.Where(j => j.Id == queued.JobId).ExecuteDeleteAsync();
    }

    [Theory]
    [InlineData("0 7 * *")]
    [InlineData("{{ $env.CRON }}")]
    public async Task Invalid_poll_cron_is_validation_error(string cron)
    {
        var editor = await LoginAsync(api);

        var problem = await AssertProblemAsync(await editor.PostAsync(Base, Json(PollNode(cron))), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");

        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("PARAMETER_INVALID", error.GetProperty("code").GetString());
        Assert.Equal("n_poll", error.GetProperty("nodeId").GetString());
        Assert.Equal("/nodes/0/parameters/cron", error.GetProperty("path").GetString());
    }

    // ---------- валідація ----------

    [Theory]
    [InlineData("0 7 * *", "/nodes/0/parameters/cron")]
    [InlineData("61 7 * * *", "/nodes/0/parameters/cron")]
    [InlineData("0 0 25 * * ?", "/nodes/0/parameters/cron")]
    [InlineData("{{ $env.CRON }}", "/nodes/0/parameters/cron")]
    public async Task Invalid_cron_is_validation_error(string cron, string path)
    {
        var editor = await LoginAsync(api);

        var problem = await AssertProblemAsync(await editor.PostAsync(Base, Json(ScheduleNode(cron))), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");

        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("PARAMETER_INVALID", error.GetProperty("code").GetString());
        Assert.Equal("n_cron", error.GetProperty("nodeId").GetString());
        Assert.Equal(path, error.GetProperty("path").GetString());

        var validate = await editor.PostAsJsonAsync($"{Base}/validate", new { definition = JsonDocument.Parse(ScheduleNode(cron)).RootElement, active = true });
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        Assert.Equal(path, Assert.Single((await JsonAsync(validate)).GetProperty("errors").EnumerateArray()).GetProperty("path").GetString());
    }

    [Fact]
    public async Task Unknown_timezone_is_validation_error_at_node_or_settings()
    {
        var editor = await LoginAsync(api);

        var own = await AssertProblemAsync(
            await editor.PostAsync(Base, Json(ScheduleNode("0 7 * * *", timezone: "Mars/Olympus"))), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Equal("/nodes/0/parameters/timezone", Assert.Single(own.GetProperty("errors").EnumerateArray()).GetProperty("path").GetString());

        var settings = await AssertProblemAsync(
            await editor.PostAsync(Base, Json(ScheduleNode("0 7 * * *", settingsTimezone: "Mars/Olympus"))), HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        Assert.Equal("/settings/timezone", Assert.Single(settings.GetProperty("errors").EnumerateArray()).GetProperty("path").GetString());
    }

    // ---------- допоміжне ----------

    private static string ScheduleNode(string cron, string? timezone = null, string settingsTimezone = "Europe/Kyiv") => Definition($$"""
        [{ "id": "n_cron", "name": "Щоранку", "type": "core.schedule", "typeVersion": 1, "position": [0, 0],
           "parameters": { "cron": "{{cron}}"{{(timezone is null ? "" : $", \"timezone\": \"{timezone}\"")}} } }]
        """, settingsTimezone);

    /// <summary><c>mcp.poll</c> із сервером-виразом: статично його не перевірити, тож реєстр серверів тесту не потрібен.</summary>
    private static string PollNode(string cron) => Definition($$$"""
        [{ "id": "n_poll", "name": "Нові замовлення", "type": "mcp.poll", "typeVersion": 1, "position": [0, 0],
           "parameters": { "serverId": "{{ 'srv' }}", "toolName": "orders", "arguments": {}, "keyField": "id", "cron": "{{{cron}}}" } }]
        """);

    private static string Definition(string nodes, string timezone = "Europe/Kyiv") => $$"""
        {
          "formatVersion": 1,
          "name": "Розклад {{Guid.NewGuid():N}}",
          "settings": { "timezone": "{{timezone}}" },
          "nodes": {{nodes}},
          "connections": []
        }
        """;

    private static async Task<IScheduler> SchedulerAsync(WebApplicationFactory<Program> factory) =>
        await factory.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();

    private static async Task<ITrigger?> TriggerAsync(WebApplicationFactory<Program> factory, Guid id) =>
        await (await SchedulerAsync(factory)).GetTrigger(QuartzScheduleRegistrar.TriggerKeyOf(id, "n_cron"));

    private async Task<ExecutionEntity> WaitForExecutionAsync(Guid workflowId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            await using (var db = api.CreateDbContext())
            {
                var execution = await db.Executions.AsNoTracking().FirstOrDefaultAsync(e => e.WorkflowId == workflowId);
                if (execution is not null)
                {
                    return execution;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "Розклад не поставив виконання вчасно.");
            await Task.Delay(200);
        }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    private static async Task<ExecutionJobPayload> PayloadAsync(OpenBpDbContext db, Guid executionId)
    {
        var payloads = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>)
            .Where(p => p.ExecutionId == executionId);
        return Assert.Single(payloads);
    }

    private async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory)
    {
        var user = await api.CreateUserAsync(UserRole.Editor, Pass);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string definition)
    {
        var response = await client.PostAsync(Base, Json(definition));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task ActivateAsync(HttpClient client, Guid id)
    {
        var response = await client.PostAsync($"{Base}/{id}/activate", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task DeactivateAsync(HttpClient client, Guid id) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{Base}/{id}/deactivate", null)).StatusCode);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private long _ticks = now.UtcTicks;

        public DateTimeOffset Now
        {
            get => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

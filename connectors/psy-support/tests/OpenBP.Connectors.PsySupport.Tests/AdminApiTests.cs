using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Hosting;

namespace OpenBP.Connectors.PsySupport.Tests;

/// <summary>REST <c>/api/v1</c> і вхід в адмінку (§7.1).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminApiTests(PostgresFixture pg)
{
    private const string AdminEmail = "coordinator@example.com";
    private const string AdminPassword = "correct-horse-battery";

    private async Task<WebApplication> StartAsync()
    {
        var cs = await pg.NewDatabaseAsync();
        var env = new Dictionary<string, string?> { ["PSY_ADMIN_EMAIL"] = AdminEmail, ["PSY_ADMIN_PASSWORD"] = AdminPassword, ["PSY_MCP_TOKEN"] = "t" };
        var app = PsyHost.BuildHttp([$"--ConnectionStrings:PsySupport={cs}"], "http://127.0.0.1:0", k => env.GetValueOrDefault(k));
        await PsyHost.InitializeAsync(app.Services);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) { BaseAddress = new Uri(app.Urls.Single()) };

    private static Task<HttpResponseMessage> LoginAsync(HttpClient http, string email, string password) =>
        http.PostAsJsonAsync("/api/v1/auth/login", new { email, password });

    [Fact]
    public async Task First_coordinator_is_seeded_and_five_failed_logins_lock_the_account()
    {
        await using var app = await StartAsync();
        using var http = Client(app);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/bookings?from=2025-01-01T00:00:00Z&to=2025-02-01T00:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(http, AdminEmail, "wrong")).StatusCode);
        for (var i = 0; i < 3; i++)
        {
            await LoginAsync(http, AdminEmail, "wrong");
        }

        Assert.Equal((HttpStatusCode)423, (await LoginAsync(http, AdminEmail, "wrong")).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await LoginAsync(http, AdminEmail, AdminPassword)).StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PsyDbContext>();
            await db.StaffAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.LockedUntil, (DateTimeOffset?)null));
        }

        var ok = await LoginAsync(http, AdminEmail.ToUpperInvariant(), AdminPassword);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var me = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("coordinator", me.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/settings")).StatusCode);
    }

    [Fact]
    public async Task Psychologist_set_up_by_invite_sees_neither_coordinator_pages_nor_colleagues_phones()
    {
        await using var app = await StartAsync();
        using var admin = Client(app);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(admin, AdminEmail, AdminPassword)).StatusCode);

        var me = (await (await admin.PostAsJsonAsync("/api/v1/psychologists", new { name = "Я", phone = "0671110000", email = "me@example.com" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var colleague = (await (await admin.PostAsJsonAsync("/api/v1/psychologists", new { name = "Колега", phone = "0672220000" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var created = await (await admin.PostAsJsonAsync("/api/v1/staff", new { email = "me@example.com", role = "psychologist", psychologist_id = me })).Content.ReadFromJsonAsync<JsonElement>();
        var token = created.GetProperty("invite").GetProperty("token").GetString();

        var starts = DateTimeOffset.UtcNow.AddDays(3);
        long Id(JsonElement e) => e.GetProperty("id").GetInt64();
        var mySlot = Id(await (await admin.PostAsJsonAsync("/api/v1/slots", new { psychologist_id = me, starts_at = starts })).Content.ReadFromJsonAsync<JsonElement>());
        var theirSlot = Id(await (await admin.PostAsJsonAsync("/api/v1/slots", new { psychologist_id = colleague, starts_at = starts.AddHours(1) })).Content.ReadFromJsonAsync<JsonElement>());
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PsyDbContext>();
            db.Users.AddRange(
                new UserEntity { TelegramId = 1, Name = "Ірина", Phone = "380501110001" },
                new UserEntity { TelegramId = 2, Name = "Петро", Phone = "380501110002" });
            db.Bookings.AddRange(new BookingEntity { SlotId = mySlot, TelegramId = 1 }, new BookingEntity { SlotId = theirSlot, TelegramId = 2 });
            await db.SaveChangesAsync();
        }

        using var psy = Client(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await psy.PostAsJsonAsync("/api/v1/auth/invite", new { token, password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await psy.PostAsJsonAsync("/api/v1/auth/invite", new { token, password = "psychologist-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await psy.PostAsJsonAsync("/api/v1/auth/invite", new { token, password = "psychologist-pass" })).StatusCode); // одноразове

        foreach (var url in new[] { "/api/v1/settings", "/api/v1/staff", "/api/v1/counters", "/api/v1/audit", "/api/v1/users?q=0501110001" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await psy.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await psy.PostAsJsonAsync("/api/v1/slots", new { psychologist_id = colleague, starts_at = starts.AddHours(5) })).StatusCode);

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(10).ToString("O"));
        var body = await psy.GetStringAsync($"/api/v1/bookings?from={from}&to={to}");
        Assert.Contains("380501110001", body, StringComparison.Ordinal);
        Assert.Contains("Ірина", body, StringComparison.Ordinal);
        Assert.DoesNotContain("380501110002", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Петро", body, StringComparison.Ordinal);
        Assert.Contains("Колега", body, StringComparison.Ordinal);

        var coordinatorView = await admin.GetStringAsync($"/api/v1/bookings?from={from}&to={to}");
        Assert.Contains("380501110002", coordinatorView, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_from_the_admin_queues_notifications_in_the_outbox()
    {
        await using var app = await StartAsync();
        using var admin = Client(app);
        await LoginAsync(admin, AdminEmail, AdminPassword);
        var pid = (await (await admin.PostAsJsonAsync("/api/v1/psychologists", new { name = "Я", phone = "0671110000" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var slot = (await (await admin.PostAsJsonAsync("/api/v1/slots", new { psychologist_id = pid, starts_at = DateTimeOffset.UtcNow.AddDays(2) })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        long booking;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PsyDbContext>();
            db.Users.Add(new UserEntity { TelegramId = 77, Name = "Марія", Phone = "380501110077" });
            var b = new BookingEntity { SlotId = slot, TelegramId = 77 };
            db.Bookings.Add(b);
            await db.SaveChangesAsync();
            booking = b.Id;
        }

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/bookings/{booking}/cancel", null)).StatusCode);

        await using var check = app.Services.CreateAsyncScope();
        var outbox = await check.ServiceProvider.GetRequiredService<PsyDbContext>().Outbox.ToListAsync();
        Assert.Equal(77, Assert.Single(outbox).ChatId);
    }
}

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Connector.Template.Tests;

/// <summary>
/// In-process мок REST API товарів на випадковому порту: запам'ятовує заголовки запитів, а
/// <see cref="FailWith"/> змушує наступні відповіді бути помилкою з заданим тілом.
/// </summary>
public sealed class MockRestApi : IAsyncDisposable
{
    private readonly WebApplication _app;

    private MockRestApi(WebApplication app) => _app = app;

    /// <summary>Заголовки <c>Authorization</c> і <c>X-API-Key</c> кожного запиту.</summary>
    public ConcurrentQueue<(string Path, string? Authorization, string? ApiKey)> Requests { get; } = new();

    /// <summary>Якщо задано — кожна відповідь має цей статус і тіло.</summary>
    public (int Status, string Body)? FailWith { get; set; }

    /// <summary>Базова адреса з кінцевим «/».</summary>
    public Uri BaseUrl => new(_app.Urls.Single() + "/api/");

    public static async Task<MockRestApi> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var mock = new MockRestApi(app);
        app.Use(async (ctx, next) =>
        {
            mock.Requests.Enqueue((ctx.Request.Path + ctx.Request.QueryString, ctx.Request.Headers.Authorization.FirstOrDefault(), ctx.Request.Headers["X-API-Key"].FirstOrDefault()));
            if (mock.FailWith is { } fail)
            {
                ctx.Response.StatusCode = fail.Status;
                await ctx.Response.WriteAsync(fail.Body);
                return;
            }

            await next(ctx);
        });
        Item[] items = [new("1", "Пшениця", 9500m), new("2", "Кукурудза", 8200m)];
        app.MapGet("/api/items", (string? search) =>
            search is null ? items : items.Where(i => i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray());
        app.MapGet("/api/items/{id}", (string id) =>
            items.FirstOrDefault(i => i.Id == id) is { } item ? Results.Ok(item) : Results.NotFound(new { message = $"item {id} not found" }));
        await app.StartAsync();
        return mock;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

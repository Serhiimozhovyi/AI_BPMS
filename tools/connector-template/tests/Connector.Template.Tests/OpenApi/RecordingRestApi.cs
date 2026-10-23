using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Connector.Template.Tests.OpenApi;

/// <summary>Запит, який отримав <see cref="RecordingRestApi"/>.</summary>
/// <param name="Method">Метод.</param>
/// <param name="Path">Шлях із query.</param>
/// <param name="Headers">Заголовки.</param>
/// <param name="ContentType">Тип тіла.</param>
/// <param name="Body">Тіло.</param>
public sealed record RecordedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string? ContentType, string Body);

/// <summary>
/// In-process мок petstore-подібного REST API на випадковому порту: запам'ятовує кожен запит, віддає
/// <c>/openapi.yaml</c> (специфікацію з тестів), а <see cref="FailWith"/> змушує відповіді бути помилкою.
/// </summary>
public sealed class RecordingRestApi : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RecordingRestApi(WebApplication app) => _app = app;

    /// <summary>Запити до <c>/api/…</c>.</summary>
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Якщо задано — кожна відповідь API має цей статус і тіло.</summary>
    public (int Status, string Body)? FailWith { get; set; }

    /// <summary>Адреса сервера без кінцевого «/».</summary>
    public string Address => _app.Urls.Single();

    /// <summary>Базова адреса API з кінцевим «/».</summary>
    public Uri BaseUrl => new(Address + "/api/");

    public static async Task<RecordingRestApi> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var mock = new RecordingRestApi(app);
        app.MapGet("/openapi.yaml", () => Results.Text(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "OpenApi", "petstore.yaml")), "application/yaml"));
        app.Map("/api/{**rest}", async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            mock.Requests.Enqueue(new RecordedRequest(
                ctx.Request.Method,
                ctx.Request.Path + ctx.Request.QueryString,
                ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                ctx.Request.ContentType,
                body));
            if (mock.FailWith is { } fail)
            {
                ctx.Response.StatusCode = fail.Status;
                await ctx.Response.WriteAsync(fail.Body);
                return;
            }

            var path = ctx.Request.Path.Value!;
            switch (ctx.Request.Method)
            {
                case "GET" when path == "/api/pets":
                    await ctx.Response.WriteAsJsonAsync(new[] { new { id = 1, name = "Rex" }, new { id = 2, name = "Mia" } });
                    break;
                case "GET" when path == "/api/pets/1":
                    await ctx.Response.WriteAsJsonAsync(new { id = 1, name = "Rex", tag = "dog" });
                    break;
                case "GET" when path == "/api/store/inventory":
                    await ctx.Response.WriteAsJsonAsync(new { available = 2, sold = 5 });
                    break;
                case "POST" when path == "/api/pets":
                    ctx.Response.StatusCode = StatusCodes.Status201Created;
                    await ctx.Response.WriteAsync("""{"id":3,"name":"Rex"}""");
                    break;
                case "DELETE" when path == "/api/pets/1":
                    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                    break;
                default:
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    await ctx.Response.WriteAsJsonAsync(new { message = $"{path} not found" });
                    break;
            }
        });
        await app.StartAsync();
        return mock;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

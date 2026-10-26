using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OpenBP.Connectors.Http.Tests;

/// <summary>Запит, що дійшов до <see cref="FakeWebServer"/>.</summary>
/// <param name="Method">Метод.</param>
/// <param name="PathAndQuery">Шлях з параметрами.</param>
/// <param name="ContentType">Тип тіла.</param>
/// <param name="Body">Тіло.</param>
/// <param name="Authorization">Заголовок Authorization.</param>
public sealed record WebRequest(string Method, string PathAndQuery, string? ContentType, string Body, string? Authorization);

/// <summary>In-process веб-сервер на випадковому порту, що віддає RSS, JSON, редіректи й помилки.</summary>
public sealed class FakeWebServer : IAsyncDisposable
{
    public const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <title>Агро новини</title>
            <link>https://agro.example/</link>
            <item>
              <title>Ціни на пшеницю</title>
              <link>https://agro.example/1</link>
              <guid>g1</guid>
              <pubDate>Mon, 26 Oct 2026 09:00:00 GMT</pubDate>
              <dc:creator>Іван</dc:creator>
              <category>зернові</category>
              <description>&lt;p&gt;Пшениця подорожчала&lt;/p&gt;</description>
            </item>
            <item>
              <title>Кукурудза</title>
              <link>https://agro.example/2</link>
              <guid>g2</guid>
              <pubDate>Sun, 25 Oct 2026 09:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private readonly WebApplication _app;

    private FakeWebServer(WebApplication app) => _app = app;

    public ConcurrentQueue<WebRequest> Requests { get; } = new();

    public string BaseUrl => _app.Urls.Single();

    public static async Task<FakeWebServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var fake = new FakeWebServer(app);
        app.Run(fake.HandleAsync);
        await app.StartAsync();
        return fake;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task HandleAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        Requests.Enqueue(new WebRequest(ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString, ctx.Request.ContentType, body, ctx.Request.Headers.Authorization.ToString()));
        switch (ctx.Request.Path.Value)
        {
            case "/feed":
                ctx.Response.ContentType = "application/rss+xml; charset=utf-8";
                await ctx.Response.WriteAsync(Rss);
                break;
            case "/json":
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{"hello":"світ","n":[1,2]}""");
                break;
            case "/text":
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("привіт");
                break;
            case "/bin":
                ctx.Response.ContentType = "image/png";
                await ctx.Response.Body.WriteAsync(new byte[] { 1, 2, 3, 4 });
                break;
            case "/big":
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.WriteAsync(new string('x', 5000));
                break;
            case "/redirect":
                ctx.Response.Redirect("/json?token=SECRET-KEY");
                break;
            case "/loop":
                ctx.Response.Redirect("/loop");
                break;
            case "/redirect-private":
                ctx.Response.Redirect("http://169.254.169.254/latest/meta-data");
                break;
            case "/missing":
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{"error":"not found"}""");
                break;
            default:
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{}");
                break;
        }
    }
}

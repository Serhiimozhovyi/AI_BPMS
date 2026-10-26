using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenBP.Shared.Validation;

namespace OpenBP.Cli.Tests;

/// <summary>Збирає справжній конектор <c>connectors/http</c> один раз на клас тестів.</summary>
public sealed class HttpConnectorFixture : IDisposable
{
    private static readonly Lock BuildLock = new();

    public HttpConnectorFixture()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("openbp-http-").FullName;
        var output = Path.Combine(Directory, "http");
        var project = Path.Combine(KitCrmFixture.RepoRoot, "connectors", "http", "src", "OpenBP.Connectors.Http", "OpenBP.Connectors.Http.csproj");
        var start = new ProcessStartInfo(KitCrmFixture.DotNet)
        {
            ArgumentList = { "build", project, "-o", output, "-nologo", "-v", "q", "-p:UseSharedCompilation=false" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        lock (BuildLock)
        {
            using var process = Process.Start(start)!;
            var log = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"Збірка http: {log.Result}{errors.Result}");
        }

        Dll = Path.Combine(output, "OpenBP.Connectors.Http.dll");
    }

    public string Directory { get; }

    public string Dll { get; }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}

/// <summary>In-process сервер: дві RSS-стрічки (<c>/a.xml</c>, <c>/b.xml</c>) і відповідь моделі <c>POST /v1/chat/completions</c>.</summary>
public sealed class AgroFakeWeb : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    public AgroFakeWeb()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        Address = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Address + "/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string Address { get; }

    /// <summary>Статті першої стрічки: (назва, посилання, чи є картинка).</summary>
    public List<(string Title, string Link, bool Image)> FeedA { get; } = [];

    public List<(string Title, string Link, bool Image)> FeedB { get; } = [];

    public string LlmAnswer { get; set; } = "<b>Пост про урожай</b>\n\nТекст. https://example.com/1";

    public ConcurrentQueue<string> LlmRequests { get; } = new();

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Слухача зупинено.
        }
    }

    private string Rss(List<(string Title, string Link, bool Image)> items)
    {
        var sb = new StringBuilder("""<?xml version="1.0"?><rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel><title>Стрічка</title><link>https://example.com</link>""");
        var day = 20;
        foreach (var (title, link, image) in items)
        {
            sb.Append($"<item><title>{title}</title><link>{link}</link><guid>{link}</guid><pubDate>Mon, {day--:00} Oct 2026 08:00:00 GMT</pubDate>")
                .Append("<description>Опис новини</description>")
                .Append("<content:encoded><![CDATA[<p>Опис новини</p>")
                .Append(image ? $"<img src=\"{Address}/pic.jpg\" />" : "")
                .Append("]]></content:encoded></item>");
        }

        return sb.Append("</channel></rss>").ToString();
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            string body;
            string type = "application/xml";
            if (path == "/v1/chat/completions" && ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                LlmRequests.Enqueue(await reader.ReadToEndAsync());
                body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = LlmAnswer } } } });
                type = "application/json";
            }
            else if (path == "/a.xml")
            {
                body = Rss(FeedA);
            }
            else if (path == "/b.xml")
            {
                body = Rss(FeedB);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = type;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }
}

/// <summary>
/// F6-29: приклад <c>examples/agro-news.json</c> через CLI зі справжніми конекторами HTTP і Telegram проти фейків:
/// RSS → merge з історією → відбір → модель → чернетка → погодження → публікація / відхилення / таймаут / без кандидатів.
/// </summary>
public sealed class AgroNewsTests(TelegramFixture telegram, HttpConnectorFixture http) : IClassFixture<TelegramFixture>, IClassFixture<HttpConnectorFixture>, IDisposable
{
    private readonly FakeTelegramApprovals _api = new();
    private readonly AgroFakeWeb _web = new();

    public void Dispose()
    {
        _api.Dispose();
        _web.Dispose();
    }

    [Fact]
    public async Task Example_is_valid_and_listed_in_templates()
    {
        var path = Path.Combine(KitCrmFixture.RepoRoot, "examples", "agro-news.json");
        Assert.Empty(await new WorkflowValidator().ValidateAsync(await File.ReadAllTextAsync(path)));

        var catalog = await File.ReadAllTextAsync(Path.Combine(KitCrmFixture.RepoRoot, "examples", "templates.json"));
        Assert.Contains("agro-news.json", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approved_post_with_image_goes_to_channel_as_photo()
    {
        _web.FeedA.Add(("Новина А", "https://example.com/a1", true));
        _web.FeedB.Add(("Новина Б", "https://example.com/b1", false));
        _api.PressOnCheck = 2;

        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        Assert.Single(_web.LlmRequests);
        var prompt = string.Concat(JsonDocument.Parse(_web.LlmRequests.Single()).RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("content").GetString()));
        Assert.Contains("Новина А", prompt, StringComparison.Ordinal);
        Assert.Contains("Новина Б", prompt, StringComparison.Ordinal);
        var texts = _api.Sent("sendMessage").Select(b => b["text"]!.GetValue<string>()).ToList();
        Assert.Equal(["🌾 Агро-чернетка\n\n<b>Пост про урожай</b>\n\nТекст. https://example.com/1", "Опублікувати цей пост?"], texts);
        var photo = Assert.Single(_api.Sent("sendPhoto"));
        Assert.Equal("@agro_channel", photo["chat_id"]!.GetValue<string>());
        Assert.Equal($"{_web.Address}/pic.jpg", photo["photo"]!.GetValue<string>());
        Assert.Contains("Пост про урожай", photo["caption"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("▶ Записати: опубліковано [core.dataTable] — Success", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ Записати: відхилено [core.dataTable] — Skipped", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approved_post_without_image_goes_to_channel_as_text()
    {
        _web.FeedA.Add(("Новина А", "https://example.com/a1", false));
        _api.PressOnCheck = 1;

        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        Assert.Empty(_api.Sent("sendPhoto"));
        Assert.Equal("@agro_channel", _api.Sent("sendMessage").Last()["chat_id"]!.GetValue<string>());
        Assert.Contains("▶ Записати: опубліковано [core.dataTable] — Success", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_post_is_recorded_and_not_published()
    {
        _web.FeedA.Add(("Новина А", "https://example.com/a1", true));
        _api.PressOnCheck = 1;
        _api.Approve = false;

        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        Assert.Empty(_api.Sent("sendPhoto"));
        Assert.DoesNotContain(_api.Sent("sendMessage"), b => b["chat_id"]!.GetValue<string>() == "@agro_channel");
        Assert.Contains("▶ Записати: відхилено [core.dataTable] — Success", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ Записати: опубліковано [core.dataTable] — Skipped", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_answer_in_four_hours_is_recorded_as_rejected()
    {
        _web.FeedA.Add(("Новина А", "https://example.com/a1", false));

        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        // Перша перевірка одразу, далі щохвилини до 4 год: 1 + 240.
        Assert.Equal(241, _api.Checks);
        Assert.DoesNotContain(_api.Sent("sendMessage"), b => b["chat_id"]!.GetValue<string>() == "@agro_channel");
        Assert.Contains("▶ Записати: відхилено [core.dataTable] — Success", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_feeds_send_a_no_post_notice_without_calling_the_model()
    {
        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        Assert.Empty(_web.LlmRequests);
        Assert.Equal("⚪️ Агро: цього разу без поста — нових новин немає", Assert.Single(_api.Sent("sendMessage"))["text"]!.GetValue<string>());
        Assert.Empty(_api.Sent("getUpdates"));
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(KitCrmFixture.RepoRoot, "examples", "agro-news.json"));
        source = source
            .Replace("https://agrotimes.ua/feed/", $"{_web.Address}/a.xml", StringComparison.Ordinal)
            .Replace("https://superagronom.com/rss/novosti", $"{_web.Address}/b.xml", StringComparison.Ordinal);
        var file = Path.Combine(telegram.Directory, $"agro-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, source);

        var servers = Path.Combine(telegram.Directory, $"servers-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(servers, JsonSerializer.Serialize(new
        {
            servers = new object[]
            {
                new
                {
                    id = "srv_telegram", transport = "stdio", command = KitCrmFixture.DotNet, args = new[] { telegram.Dll },
                    env = new Dictionary<string, string>
                    {
                        ["Connector__BaseUrl"] = _api.Address,
                        ["TELEGRAM_BOT_TOKEN"] = FakeTelegramApprovals.Token,
                        ["Logging__LogLevel__Default"] = "Warning",
                    },
                },
                new
                {
                    id = "srv_http", transport = "stdio", command = KitCrmFixture.DotNet, args = new[] { http.Dll },
                    env = new Dictionary<string, string> { ["HTTP_ALLOW_PRIVATE"] = "127.0.0.1", ["Logging__LogLevel__Default"] = "Warning" },
                },
            },
        }));
        var credentials = Path.Combine(telegram.Directory, $"creds-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(credentials, JsonSerializer.Serialize(new { cred_ollama = new { baseUrl = $"{_web.Address}/v1" } }));

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApp.RunAsync(["run", file, "--servers", servers, "--credentials", credentials], stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }
}

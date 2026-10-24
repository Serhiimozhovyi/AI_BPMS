using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBP.Cli;

/// <summary>
/// Команди проти запущеного OpenBP через REST (F6-09) — для CI/CD: <c>export</c>, <c>import</c>, <c>activate</c>.
/// Адреса — <c>--url</c> або <c>OPENBP_URL</c>, ключ — <c>--api-key</c> або <c>OPENBP_API_KEY</c> (у CI краще змінна
/// середовища з секрету, щоб ключ не потрапляв у журнал команд). Ключ іде лише в заголовок <c>Authorization: Bearer</c>
/// і ніде не друкується.
/// </summary>
internal static class RemoteCommands
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal) { "export", "import", "activate" };

    private const int PageSize = 100;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, HttpMessageHandler? handler, CancellationToken ct)
    {
        var options = RemoteOptions.Parse(args);
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = options.Url;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return options.Command switch
        {
            "export" => await ExportAsync(http, options, stdout, ct).ConfigureAwait(false),
            "import" => await ImportAsync(http, options, stdout, ct).ConfigureAwait(false),
            _ => await ActivateAsync(http, options, stdout, ct).ConfigureAwait(false),
        };
    }

    private static async Task<int> ExportAsync(HttpClient http, RemoteOptions options, TextWriter stdout, CancellationToken ct)
    {
        var ids = options.All ? await ListWorkflowIdsAsync(http, ct).ConfigureAwait(false) : [ParseId(options.Target!)];
        var dir = options.Out ?? ".";
        Directory.CreateDirectory(dir);
        foreach (var id in ids)
        {
            var export = await SendAsync(http, HttpMethod.Get, $"api/v1/workflows/{id}/export", null, ct).ConfigureAwait(false);
            var path = Path.Combine(dir, $"{id}.json");
            await File.WriteAllTextAsync(path, export.ToJsonString(Indented) + "\n", ct).ConfigureAwait(false);
            await stdout.WriteLineAsync($"{path}  {export["definition"]?["name"]}").ConfigureAwait(false);
        }

        await stdout.WriteLineAsync($"Експортовано сценаріїв: {ids.Count}.").ConfigureAwait(false);
        return CliApp.Ok;
    }

    private static async Task<int> ImportAsync(HttpClient http, RemoteOptions options, TextWriter stdout, CancellationToken ct)
    {
        var body = ReadObject(options.Target!, "експорту");
        if (body["definition"] is null)
        {
            // Файл — саме визначення без маніфесту: посилання мають уже бути id цього середовища.
            body = new JsonObject { ["definition"] = body };
        }

        if (options.Map is { } map)
        {
            body["mapping"] = ReadObject(map, "відповідників");
        }

        var response = await SendRawAsync(http, HttpMethod.Post, "api/v1/workflows/import", body, ct).ConfigureAwait(false);
        if (response.Status == HttpStatusCode.UnprocessableEntity && response.Body?["code"]?.GetValue<string>() == "IMPORT_UNMAPPED")
        {
            await stdout.WriteLineAsync("Немає відповідників у цьому середовищі (додайте їх у --map):").ConfigureAwait(false);
            foreach (var u in response.Body["unmapped"]!.AsArray())
            {
                await stdout.WriteLineAsync($"  {u!["kind"]} {u["ref"]} «{u["name"]}» ({u["type"]}): {u["reason"]}").ConfigureAwait(false);
            }

            return CliApp.ExecutionFailed;
        }

        var result = EnsureSuccess(response);
        var workflow = result["workflow"]!;
        await stdout.WriteLineAsync($"Імпортовано: {workflow["id"]}  {workflow["name"]}").ConfigureAwait(false);
        if (result["missingVariables"] is JsonArray { Count: > 0 } missing)
        {
            await stdout.WriteLineAsync($"Відсутні змінні: {string.Join(", ", missing.Select(v => v!.GetValue<string>()))}").ConfigureAwait(false);
        }

        return CliApp.Ok;
    }

    private static async Task<int> ActivateAsync(HttpClient http, RemoteOptions options, TextWriter stdout, CancellationToken ct)
    {
        var id = ParseId(options.Target!);
        var workflow = await SendAsync(http, HttpMethod.Post, $"api/v1/workflows/{id}/activate", null, ct).ConfigureAwait(false);
        await stdout.WriteLineAsync($"Активовано: {workflow["id"]}  {workflow["name"]}").ConfigureAwait(false);
        return CliApp.Ok;
    }

    private static async Task<List<Guid>> ListWorkflowIdsAsync(HttpClient http, CancellationToken ct)
    {
        var ids = new List<Guid>();
        for (var page = 1; ; page++)
        {
            var result = await SendAsync(http, HttpMethod.Get, $"api/v1/workflows?page={page}&pageSize={PageSize}", null, ct).ConfigureAwait(false);
            var items = result["items"]!.AsArray();
            ids.AddRange(items.Select(i => Guid.Parse(i!["id"]!.GetValue<string>())));
            if (items.Count < PageSize || ids.Count >= result["total"]!.GetValue<int>())
            {
                return ids;
            }
        }
    }

    private static async Task<JsonNode> SendAsync(HttpClient http, HttpMethod method, string path, JsonNode? body, CancellationToken ct) =>
        EnsureSuccess(await SendRawAsync(http, method, path, body, ct).ConfigureAwait(false));

    private static async Task<RawResponse> SendRawAsync(HttpClient http, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? json = null;
            try
            {
                json = text.Length == 0 ? null : JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // Не JSON (проксі, HTML-сторінка помилки) — повідомлення збере EnsureSuccess.
            }

            return new RawResponse(response.StatusCode, json, response.IsSuccessStatusCode);
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"OpenBP недоступний ({http.BaseAddress}): {ex.Message}");
        }
    }

    private static JsonNode EnsureSuccess(RawResponse response)
    {
        if (response.Success && response.Body is not null)
        {
            return response.Body;
        }

        var detail = response.Body?["detail"]?.GetValue<string>() ?? "без пояснення";
        var code = response.Body?["code"]?.GetValue<string>();
        var errors = response.Body?["errors"] is JsonArray list
            ? "\n" + string.Join("\n", list.Select(e => $"  {e!["code"]}: {e["message"]}"))
            : "";
        throw new CliException($"OpenBP відповів {(int)response.Status}{(code is null ? "" : $" {code}")}: {detail}{errors}");
    }

    private static JsonObject ReadObject(string path, string what)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new CliException($"Файл {what} {path} має містити JSON-об'єкт.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CliException($"Не вдалося прочитати файл {what} {path}: {ex.Message}");
        }
    }

    private static Guid ParseId(string value) =>
        Guid.TryParse(value, out var id) ? id : throw new CliException($"«{value}» — не id сценарію.", showUsage: true);

    private sealed record RawResponse(HttpStatusCode Status, JsonNode? Body, bool Success);

    private sealed record RemoteOptions(string Command, string? Target, bool All, Uri Url, string ApiKey, string? Out, string? Map)
    {
        public static RemoteOptions Parse(string[] args)
        {
            var command = args[0];
            string? target = null, url = null, key = null, output = null, map = null;
            var all = false;
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--all" when command == "export":
                        all = true;
                        break;
                    case "--url" or "--api-key" or "--out" or "--map" when i + 1 >= args.Length:
                        throw new CliException($"Після {args[i]} очікується значення.", showUsage: true);
                    case "--url":
                        url = args[++i];
                        break;
                    case "--api-key":
                        key = args[++i];
                        break;
                    case "--out" when command == "export":
                        output = args[++i];
                        break;
                    case "--map" when command == "import":
                        map = args[++i];
                        break;
                    case var a when a.StartsWith("--", StringComparison.Ordinal):
                        throw new CliException($"Невідомий параметр «{a}» для {command}.", showUsage: true);
                    case var a when target is null:
                        target = a;
                        break;
                    default:
                        throw new CliException($"Зайвий аргумент «{args[i]}».", showUsage: true);
                }
            }

            if (all == (target is not null) && command == "export")
            {
                throw new CliException("Для export вкажіть id сценарію або --all.", showUsage: true);
            }

            if (target is null && command != "export")
            {
                throw new CliException(command == "import" ? "Не вказано файл експорту." : "Не вказано id сценарію.", showUsage: true);
            }

            url ??= Environment.GetEnvironmentVariable("OPENBP_URL");
            key ??= Environment.GetEnvironmentVariable("OPENBP_API_KEY");
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var baseUri))
            {
                throw new CliException("Вкажіть адресу OpenBP: --url або OPENBP_URL (напр. https://openbp.example.com).", showUsage: true);
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new CliException("Вкажіть API-ключ: --api-key або OPENBP_API_KEY.", showUsage: true);
            }

            return new RemoteOptions(command, target, all, baseUri, key, output, map);
        }
    }
}

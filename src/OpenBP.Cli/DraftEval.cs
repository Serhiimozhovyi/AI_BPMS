using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBP.Cli;

/// <summary>
/// <c>openbp draft-eval --workflow &lt;id&gt;</c> (F3-105) — прогін еталонних описів (<c>tests/drafter-cases</c>) через
/// <c>POST /workflows/{id}/draft</c> запущеного AI_BPMS. Модель — та, що вибрана в «Налаштування → Помічник»; сценарій
/// <c>--workflow</c> не змінюється (побудова нічого не зберігає). Друкує таблицю: валідність, збіг структури з очікуваннями,
/// повтори (кроки <c>retry</c>), час; і підсумок. Ключ іде лише в заголовок і ніде не друкується.
/// </summary>
internal static class DraftEval
{
    public const string Name = "draft-eval";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, HttpMessageHandler? handler, CancellationToken ct)
    {
        var options = EvalOptions.Parse(args);
        var cases = LoadCases(options.CasesDir, options.Only);
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = options.Url;
        http.Timeout = TimeSpan.FromMinutes(7);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var model = options.Model ?? await AssistantModelAsync(http, ct).ConfigureAwait(false) ?? "—";
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Модель: {model}; описів: {cases.Count}");
        report.AppendLine();
        report.AppendLine("| Опис | Валідний | Структура | Повторів | Час, с |");
        report.AppendLine("|---|---|---|---|---|");

        var results = new List<CaseResult>();
        foreach (var item in cases)
        {
            var result = await RunCaseAsync(http, options.Workflow, item, ct).ConfigureAwait(false);
            results.Add(result);
            var structure = result.Problems.Count == 0 ? "так" : "ні: " + string.Join("; ", result.Problems);
            report.AppendLine(CultureInfo.InvariantCulture, $"| {result.Id} | {(result.Valid ? "так" : "ні")} | {structure} | {result.Retries} | {Seconds(result.Seconds)} |");
        }

        var built = results.Count(r => r.Error is null);
        var average = results.Count == 0 ? 0 : results.Average(r => r.Seconds);
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Валідних сценаріїв: {results.Count(r => r.Valid)} з {results.Count}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Збігів структури: {results.Count(r => r.Valid && r.Problems.Count == 0)} з {results.Count}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Повторів: {results.Sum(r => r.Retries)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Середній час, с: {Seconds(average)}");
        foreach (var failed in results.Where(r => r.Error is not null))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Без сценарію: {failed.Id} — {failed.Error}");
        }

        var text = report.ToString();
        await stdout.WriteAsync(text).ConfigureAwait(false);
        if (options.Out is not null)
        {
            await File.WriteAllTextAsync(options.Out, text, ct).ConfigureAwait(false);
        }

        return built == 0 && results.Count > 0 ? CliApp.ExecutionFailed : CliApp.Ok;
    }

    /// <summary>Читає випадки з теки: <c>id</c>, <c>description</c>, <c>expect</c>; порядок — за іменем файлу.</summary>
    internal static IReadOnlyList<DraftCase> LoadCases(string dir, string? only)
    {
        if (!Directory.Exists(dir))
        {
            throw new CliException($"Немає теки з описами: {dir}.");
        }

        var cases = new List<DraftCase>();
        foreach (var path in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            JsonObject json;
            try
            {
                json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                    ?? throw new CliException($"Файл {path} має містити JSON-об'єкт.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new CliException($"Не вдалося прочитати {path}: {ex.Message}");
            }

            var id = json["id"]?.GetValue<string>();
            var description = json["description"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(description))
            {
                throw new CliException($"У {path} мають бути непорожні id та description.");
            }

            if (only is null || id.Contains(only, StringComparison.OrdinalIgnoreCase))
            {
                cases.Add(new DraftCase(id, description, json["expect"]));
            }
        }

        if (cases.Count == 0)
        {
            throw new CliException(only is null ? $"У {dir} немає описів (*.json)." : $"Жоден опис не підходить під «{only}».");
        }

        return cases;
    }

    /// <summary>Порівнює сценарій з очікуваннями; порожній список — збіг.</summary>
    internal static IReadOnlyList<string> Check(JsonNode definition, JsonNode? expect)
    {
        var problems = new List<string>();
        if (expect is null)
        {
            return problems;
        }

        var nodes = (definition["nodes"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var connections = (definition["connections"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var types = nodes.Select(n => n["type"]?.GetValue<string>() ?? "").ToList();

        if (expect["trigger"] is JsonArray { Count: > 0 } triggers)
        {
            var allowed = triggers.Select(t => t!.GetValue<string>()).ToList();
            var targets = connections.Select(c => c["to"]?["node"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var roots = nodes.Where(n => !targets.Contains(n["id"]?.GetValue<string>())).Select(n => n["type"]?.GetValue<string>() ?? "").ToList();
            if (!roots.Any(allowed.Contains))
            {
                problems.Add($"тригер {(roots.Count == 0 ? "відсутній" : string.Join("/", roots))}, очікувався {string.Join("/", allowed)}");
            }
        }

        if (expect["requireTypes"] is JsonArray groups)
        {
            foreach (var group in groups.OfType<JsonArray>())
            {
                var any = group.Select(t => t!.GetValue<string>()).ToList();
                if (!any.Any(types.Contains))
                {
                    problems.Add($"немає жодного з {string.Join("/", any)}");
                }
            }
        }

        if (expect["minNodes"] is { } min && nodes.Count < min.GetValue<int>())
        {
            problems.Add($"вузлів {nodes.Count}, мінімум {min.GetValue<int>()}");
        }

        if (expect["maxNodes"] is { } max && nodes.Count > max.GetValue<int>())
        {
            problems.Add($"вузлів {nodes.Count}, максимум {max.GetValue<int>()}");
        }

        if (expect["minBranches"] is { } minBranches)
        {
            var branches = connections
                .GroupBy(c => c["from"]?["node"]?.GetValue<string>() ?? "", StringComparer.Ordinal)
                .Count(g => g.Select(c => c["from"]?["output"]?.GetValue<int>() ?? 0).Distinct().Count() >= 2);
            if (branches < minBranches.GetValue<int>())
            {
                problems.Add($"розгалужень {branches}, мінімум {minBranches.GetValue<int>()}");
            }
        }

        if (expect["minLanes"] is { } minLanes)
        {
            var lanes = (definition["business"]?["lanes"] as JsonArray)?.Count ?? 0;
            if (lanes < minLanes.GetValue<int>())
            {
                problems.Add($"доріжок {lanes}, мінімум {minLanes.GetValue<int>()}");
            }
        }

        return problems;
    }

    private static async Task<CaseResult> RunCaseAsync(HttpClient http, Guid workflow, DraftCase item, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/workflows/{workflow}/draft")
            {
                Content = new StringContent(new JsonObject { ["description"] = item.Description }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = TryParse(text)?["detail"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}";
                return Failed(item, clock, detail);
            }

            JsonNode? definition = null;
            string? error = null;
            var retries = 0;
            foreach (var data in Events(text))
            {
                switch (data["type"]?.GetValue<string>())
                {
                    case "definition":
                        definition = data["definition"];
                        break;
                    case "error":
                        error = data["text"]?.GetValue<string>();
                        break;
                    case "step" when data["stage"]?.GetValue<string>() == "retry":
                        retries++;
                        break;
                }
            }

            if (definition is null)
            {
                return Failed(item, clock, error ?? "сценарій не побудовано") with { Retries = retries };
            }

            var valid = await IsValidAsync(http, definition, ct).ConfigureAwait(false);
            return new CaseResult(item.Id, valid, Check(definition, item.Expect), retries, clock.Elapsed.TotalSeconds, null);
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"AI_BPMS недоступний ({http.BaseAddress}): {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(item, clock, "час очікування вичерпано");
        }
    }

    private static CaseResult Failed(DraftCase item, Stopwatch clock, string error) =>
        new(item.Id, false, ["сценарій не побудовано"], 0, clock.Elapsed.TotalSeconds, error);

    private static async Task<bool> IsValidAsync(HttpClient http, JsonNode definition, CancellationToken ct)
    {
        var body = new JsonObject { ["definition"] = definition.DeepClone() };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/workflows/validate")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var json = TryParse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return response.IsSuccessStatusCode && json?["isValid"]?.GetValue<bool>() == true;
    }

    private static async Task<string?> AssistantModelAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("api/v1/settings/assistant", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? TryParse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["model"]?.GetValue<string>()
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonNode> Events(string text)
    {
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var data = string.Join("\n", block.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => l["data: ".Length..]));
            if (TryParse(data) is { } json)
            {
                yield return json;
            }
        }
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    internal sealed record DraftCase(string Id, string Description, JsonNode? Expect);

    private sealed record CaseResult(string Id, bool Valid, IReadOnlyList<string> Problems, int Retries, double Seconds, string? Error);

    private sealed record EvalOptions(Guid Workflow, Uri Url, string ApiKey, string CasesDir, string? Model, string? Out, string? Only)
    {
        public static EvalOptions Parse(string[] args)
        {
            string? workflow = null, url = null, key = null, cases = null, model = null, output = null, only = null;
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--workflow" or "--url" or "--api-key" or "--cases" or "--model" or "--out" or "--only" when i + 1 >= args.Length:
                        throw new CliException($"Після {args[i]} очікується значення.", showUsage: true);
                    case "--workflow":
                        workflow = args[++i];
                        break;
                    case "--url":
                        url = args[++i];
                        break;
                    case "--api-key":
                        key = args[++i];
                        break;
                    case "--cases":
                        cases = args[++i];
                        break;
                    case "--model":
                        model = args[++i];
                        break;
                    case "--out":
                        output = args[++i];
                        break;
                    case "--only":
                        only = args[++i];
                        break;
                    default:
                        throw new CliException($"Невідомий аргумент «{args[i]}» для {Name}.", showUsage: true);
                }
            }

            if (!Guid.TryParse(workflow, out var workflowId))
            {
                throw new CliException("Вкажіть --workflow <id сценарію>: будь-який сценарій, його вміст не змінюється.", showUsage: true);
            }

            url ??= Environment.GetEnvironmentVariable("OPENBP_URL");
            key ??= Environment.GetEnvironmentVariable("OPENBP_API_KEY");
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var baseUri))
            {
                throw new CliException("Вкажіть адресу AI_BPMS: --url або OPENBP_URL (напр. https://openbp.example.com).", showUsage: true);
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new CliException("Вкажіть API-ключ: --api-key або OPENBP_API_KEY.", showUsage: true);
            }

            return new EvalOptions(workflowId, baseUri, key, cases ?? Path.Combine("tests", "drafter-cases"), model, output, only);
        }
    }
}

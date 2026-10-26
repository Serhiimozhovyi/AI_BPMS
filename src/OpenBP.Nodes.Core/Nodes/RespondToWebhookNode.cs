using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Webhooks;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.respondToWebhook</c> (F6-05) — відповідь викликачу webhook-а з <c>respond: fromNode</c>: статус, заголовки й
/// тіло з параметрів першого вхідного item'а (тіло — рядок як текст, інше як JSON; без тіла — <c>json</c> item'а; або
/// файл <c>binaryKey</c> з його MIME-типом). Відповідь пише <see cref="IWebhookResponder"/>, виконання триває далі,
/// items проходять на вихід без змін. Відповідає лише перший такий вузол у виконанні.
/// </summary>
public sealed class RespondToWebhookNode(IWebhookResponder? responder = null) : INode
{
    /// <summary>Тип вузла.</summary>
    public const string Type = "core.respondToWebhook";

    /// <summary>Найбільший розмір тіла відповіді.</summary>
    public const int MaxBodyBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Transfer-Encoding", "Connection",
    };

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: Type,
        Version: 1,
        DisplayName: "Відповідь webhook-у",
        Group: NodeGroup.Logic,
        Inputs: ["main"],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("respondToWebhook"),
        Description: "Відповідає тому, хто викликав webhook (режим «Відповідь з вузла»): статус, заголовки, тіло або файл; сценарій виконується далі.");

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var target = responder ?? throw new CoreNodeException("Відповідь webhook-у тут недоступна: її надсилає воркер OpenBP. Запустіть сценарій на сервері.");
        var items = ctx.Items();
        var first = items.Count > 0 ? items[0] : new Item();
        var p = ctx.Params<RespondParameters>(0);
        if (p.StatusCode is < 100 or > 599)
        {
            throw new CoreNodeException($"HTTP-статус {p.StatusCode} поза межами 100…599. Вкажіть, напр., 200 або 404.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in p.Headers ?? [])
        {
            if (!Ignored.Contains(name))
            {
                headers[name] = value;
            }
        }

        byte[] body;
        string contentType;
        if (!string.IsNullOrWhiteSpace(p.BinaryKey))
        {
            if (first.Binary?.TryGetValue(p.BinaryKey, out var file) != true)
            {
                throw new CoreNodeException($"У першого item'а немає файлу «{p.BinaryKey}». Перевірте ключ у binary попереднього вузла.");
            }

            if (file!.FileSize is > MaxBodyBytes)
            {
                throw TooLarge();
            }

            await using var stream = await ctx.Binary.OpenReadAsync(file, ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            body = buffer.ToArray();
            contentType = file.MimeType;
            if (file.FileName is { Length: > 0 } fileName && !headers.ContainsKey("Content-Disposition"))
            {
                headers["Content-Disposition"] = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
            }
        }
        else if (p.Body is JsonValue text && text.TryGetValue<string>(out var s))
        {
            body = Encoding.UTF8.GetBytes(s);
            contentType = "text/plain; charset=utf-8";
        }
        else
        {
            body = JsonSerializer.SerializeToUtf8Bytes(p.Body ?? first.Json, WorkflowJson.Options);
            contentType = "application/json; charset=utf-8";
        }

        if (body.Length > MaxBodyBytes)
        {
            throw TooLarge();
        }

        headers.TryAdd("Content-Type", contentType);
        await target.RespondAsync(ctx.ExecutionId, new WebhookResponse(p.StatusCode, headers, body), ct).ConfigureAwait(false);
        return [[.. items.Select(ItemCopy.Clone)]];
    }

    private static CoreNodeException TooLarge() =>
        new($"Тіло відповіді більше за {MaxBodyBytes / 1024 / 1024} МБ. Віддайте посилання на файл замість самого файлу.");

    private sealed class RespondParameters
    {
        public int StatusCode { get; set; } = 200;

        public Dictionary<string, string>? Headers { get; set; }

        public JsonNode? Body { get; set; }

        public string? BinaryKey { get; set; }
    }
}

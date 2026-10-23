using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Connector.Template.Generated;

/// <summary>
/// Згенерований tool над REST API: будує HTTP-запит з аргументів і викликає його через <see cref="RestClient"/>
/// (автентифікація й очищення секретів — ті самі, що в ручних tools). JSON-об'єкт у відповіді — <c>structuredContent</c>
/// (форму задає <see cref="Shape"/>), масив — <c>{ "result": [...] }</c>; помилка REST чи аргументів — <c>isError</c>.
/// </summary>
/// <param name="tool">Опис для <c>tools/list</c>.</param>
/// <param name="rest">Клієнт REST API.</param>
public abstract class RestTool(Tool tool, Func<RestClient> rest) : McpServerTool
{
    /// <inheritdoc />
    public override Tool ProtocolTool => tool;

    /// <inheritdoc />
    public override IReadOnlyList<object> Metadata => [];

    /// <inheritdoc />
    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var arguments = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        try
        {
            using var message = BuildRequest(arguments);
            using var response = await rest().SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ToResult((int)response.StatusCode, body);
        }
        catch (ConnectorException ex)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
        }
    }

    /// <summary>HTTP-запит з аргументів (шлях — відносно базової адреси конектора).</summary>
    /// <param name="arguments">Аргументи <c>tools/call</c>.</param>
    /// <exception cref="ConnectorException">Аргументи неповні чи невалідні.</exception>
    public abstract HttpRequestMessage BuildRequest(IDictionary<string, JsonElement> arguments);

    /// <summary><c>structuredContent</c> з JSON-відповіді: об'єкт — як є, масив — <c>{ "result": [...] }</c>, інше — без нього.</summary>
    /// <param name="json">Відповідь.</param>
    protected virtual JsonElement? Shape(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Object => json,
        JsonValueKind.Array => JsonSerializer.SerializeToElement(new { result = json }),
        _ => null,
    };

    /// <summary>Значення аргументу або <c>null</c>, якщо його немає чи він <c>null</c>.</summary>
    /// <param name="arguments">Аргументи.</param>
    /// <param name="name">Назва.</param>
    protected static JsonElement? Argument(IDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;

    /// <summary>Рядок зі скалярного аргументу: рядок — без лапок, інше — JSON-текст.</summary>
    /// <param name="value">Значення.</param>
    protected static string Scalar(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private CallToolResult ToResult(int status, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new CallToolResult { Content = [new TextContentBlock { Text = $"HTTP {status}" }] };
        }

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException)
        {
            return new CallToolResult { Content = [new TextContentBlock { Text = body }] };
        }

        var structured = Shape(json);
        return new CallToolResult
        {
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured?.GetRawText() ?? body }],
        };
    }
}

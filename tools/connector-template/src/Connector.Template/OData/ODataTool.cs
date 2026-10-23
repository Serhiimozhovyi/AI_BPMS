using System.Globalization;
using System.Text;
using System.Text.Json;
using Connector.Template.Generated;
using ModelContextProtocol.Protocol;

namespace Connector.Template.OData;

/// <summary>Операція над entity set.</summary>
public enum ODataOperation
{
    /// <summary><c>GET Set?$filter…</c>.</summary>
    List,

    /// <summary><c>GET Set(key)</c>.</summary>
    Get,

    /// <summary><c>POST Set</c>.</summary>
    Create,

    /// <summary><c>PATCH Set(key)</c>.</summary>
    Update,
}

/// <summary>Що потрібно, щоб скласти URL entity set.</summary>
/// <param name="EntitySet">Назва entity set (сегмент URL).</param>
/// <param name="Keys">Ключові властивості.</param>
/// <param name="IsV4">OData v4 (інакше — літерали v3).</param>
/// <param name="FormatParameter">Додавати <c>$format=json</c>.</param>
public sealed record ODataBinding(string EntitySet, IReadOnlyList<CsdlProperty> Keys, bool IsV4, bool FormatParameter);

/// <summary>
/// Згенерований tool entity set OData: складає URL з ключем і системними параметрами запиту, тіло — JSON сутності
/// (<see cref="RestTool"/>). Відповідь колекції <c>{ "value": [...] }</c> стає <c>{ "result": [...] }</c>.
/// </summary>
/// <param name="tool">Опис для <c>tools/list</c>.</param>
/// <param name="operation">Операція.</param>
/// <param name="binding">Entity set і ключі.</param>
/// <param name="rest">Клієнт сервісу.</param>
public sealed class ODataTool(Tool tool, ODataOperation operation, ODataBinding binding, Func<RestClient> rest) : RestTool(tool, rest)
{
    /// <summary>Операція.</summary>
    public ODataOperation Operation => operation;

    /// <inheritdoc />
    public override HttpRequestMessage BuildRequest(IDictionary<string, JsonElement> arguments)
    {
        var path = Uri.EscapeDataString(binding.EntitySet);
        if (operation is ODataOperation.Get or ODataOperation.Update)
        {
            path += KeySegment(arguments);
        }

        var query = new List<string>();
        if (operation == ODataOperation.List)
        {
            AddOption(query, "$filter", Argument(arguments, "filter") is { } f ? Scalar(f) : null);
            AddOption(query, "$orderby", Argument(arguments, "orderby") is { } o ? Scalar(o) : null);
            AddOption(query, "$top", Argument(arguments, "top") is { } t ? Integer(t, "top") : null);
            AddOption(query, "$skip", Argument(arguments, "skip") is { } s ? Integer(s, "skip") : null);
        }

        if (operation is ODataOperation.List or ODataOperation.Get)
        {
            AddOption(query, "$select", List(arguments, "select"));
            AddOption(query, "$expand", List(arguments, "expand"));
        }

        if (binding.FormatParameter)
        {
            query.Add("$format=json");
        }

        var method = operation switch
        {
            ODataOperation.Create => HttpMethod.Post,
            ODataOperation.Update => HttpMethod.Patch,
            _ => HttpMethod.Get,
        };
        var message = new HttpRequestMessage(method, new Uri(path + (query.Count > 0 ? "?" + string.Join('&', query) : ""), UriKind.Relative));
        message.Headers.Accept.ParseAdd("application/json");

        if (operation is ODataOperation.Create or ODataOperation.Update)
        {
            if (Argument(arguments, ODataToolFactory.BodyArgument) is not { ValueKind: JsonValueKind.Object } body)
            {
                throw new ConnectorException($"Аргумент «{ODataToolFactory.BodyArgument}» — об'єкт із властивостями сутності.");
            }

            message.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        }

        return message;
    }

    /// <inheritdoc />
    protected override JsonElement? Shape(JsonElement json) =>
        operation == ODataOperation.List && json.ValueKind == JsonValueKind.Object && json.TryGetProperty("value", out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? JsonSerializer.SerializeToElement(new { result = value })
            : base.Shape(json);

    /// <summary><c>(literal)</c> для одного ключа, <c>(K1=literal,K2=literal)</c> — для складеного.</summary>
    private string KeySegment(IDictionary<string, JsonElement> arguments)
    {
        var parts = new List<string>();
        foreach (var key in binding.Keys)
        {
            if (Argument(arguments, key.Name) is not { } value)
            {
                throw new ConnectorException($"Не задано обов'язковий аргумент «{key.Name}» (ключ {binding.EntitySet}).");
            }

            var literal = Literal(key.Type, value);
            parts.Add(binding.Keys.Count == 1 ? literal : $"{Uri.EscapeDataString(key.Name)}={literal}");
        }

        return $"({string.Join(',', parts)})";
    }

    /// <summary>Літерал ключа в URL за типом Edm (v3 — з префіксами <c>guid'…'</c>, <c>datetime'…'</c>).</summary>
    private string Literal(string type, JsonElement value)
    {
        var text = Scalar(value);
        return type switch
        {
            "Edm.String" => $"'{Uri.EscapeDataString(text.Replace("'", "''", StringComparison.Ordinal))}'",
            "Edm.Guid" when !binding.IsV4 => $"guid'{Uri.EscapeDataString(text)}'",
            "Edm.DateTime" when !binding.IsV4 => $"datetime'{Uri.EscapeDataString(text)}'",
            "Edm.DateTimeOffset" when !binding.IsV4 => $"datetimeoffset'{Uri.EscapeDataString(text)}'",
            "Edm.Int64" when !binding.IsV4 => $"{Uri.EscapeDataString(text)}L",
            "Edm.Decimal" when !binding.IsV4 => $"{Uri.EscapeDataString(text)}M",
            _ => Uri.EscapeDataString(text),
        };
    }

    private static void AddOption(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private static string? List(IDictionary<string, JsonElement> arguments, string name) => Argument(arguments, name) switch
    {
        { ValueKind: JsonValueKind.Array } array => string.Join(',', array.EnumerateArray().Select(Scalar)),
        { } single => Scalar(single),
        null => null,
    };

    private static string Integer(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= 0
            ? n.ToString(CultureInfo.InvariantCulture)
            : throw new ConnectorException($"Аргумент «{name}» — невід'ємне ціле число.");
}

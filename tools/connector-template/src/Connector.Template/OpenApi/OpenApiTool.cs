using System.Text;
using System.Text.Json;
using Connector.Template.Generated;
using ModelContextProtocol.Protocol;

namespace Connector.Template.OpenApi;

/// <summary>Як аргументи tool'а лягають у HTTP-запит операції.</summary>
/// <param name="Method">HTTP-метод.</param>
/// <param name="Path">Шаблон шляху, напр. <c>/pets/{petId}</c>.</param>
/// <param name="Parameters">Параметри path/query/header.</param>
/// <param name="BodyArgument">Аргумент із JSON-тілом або <c>null</c>.</param>
public sealed record OpenApiBinding(HttpMethod Method, string Path, IReadOnlyList<OpenApiBinding.Parameter> Parameters, string? BodyArgument)
{
    /// <summary>Параметр операції.</summary>
    /// <param name="Name">Назва в HTTP.</param>
    /// <param name="Argument">Назва аргументу tool'а.</param>
    /// <param name="In">Де передається.</param>
    /// <param name="Required">Обов'язковий.</param>
    public sealed record Parameter(string Name, string Argument, Microsoft.OpenApi.ParameterLocation In, bool Required);
}

/// <summary>
/// Згенерований tool операції OpenAPI: підставляє аргументи в шлях, query, заголовки й тіло (<see cref="RestTool"/>).
/// </summary>
/// <param name="tool">Опис для <c>tools/list</c>.</param>
/// <param name="binding">Прив'язка аргументів.</param>
/// <param name="rest">Клієнт REST API.</param>
public sealed class OpenApiTool(Tool tool, OpenApiBinding binding, Func<RestClient> rest) : RestTool(tool, rest)
{
    /// <summary>Прив'язка аргументів до запиту.</summary>
    public OpenApiBinding Binding => binding;

    /// <inheritdoc />
    public override HttpRequestMessage BuildRequest(IDictionary<string, JsonElement> arguments)
    {
        var path = binding.Path;
        var query = new List<string>();
        var headers = new List<(string Name, string Value)>();
        foreach (var parameter in binding.Parameters)
        {
            if (Argument(arguments, parameter.Argument) is not { } value)
            {
                if (parameter.Required)
                {
                    throw new ConnectorException($"Не задано обов'язковий аргумент «{parameter.Argument}».");
                }

                continue;
            }

            switch (parameter.In)
            {
                case Microsoft.OpenApi.ParameterLocation.Path:
                    path = path.Replace($"{{{parameter.Name}}}", Uri.EscapeDataString(string.Join(',', Values(value))), StringComparison.Ordinal);
                    break;
                case Microsoft.OpenApi.ParameterLocation.Query:
                    // style=form, explode=true (типово в OpenAPI): масив — повторений параметр.
                    query.AddRange(Values(value).Select(v => $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(v)}"));
                    break;
                case Microsoft.OpenApi.ParameterLocation.Header:
                    headers.Add((parameter.Name, string.Join(',', Values(value))));
                    break;
            }
        }

        // Відносний шлях без «/» на початку — інакше він замінить шлях базової адреси.
        var uri = path.TrimStart('/') + (query.Count > 0 ? "?" + string.Join('&', query) : "");
        var message = new HttpRequestMessage(binding.Method, new Uri(uri, UriKind.Relative));
        foreach (var (name, value) in headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (binding.BodyArgument is { } bodyArgument && arguments.TryGetValue(bodyArgument, out var body) &&
            body.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            message.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        }

        return message;
    }

    private static IEnumerable<string> Values(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().Select(Scalar),
        _ => [Scalar(value)],
    };
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Connector.Template.Tests.OData;

/// <summary>Запит, який отримав <see cref="MockODataApi"/>: <see cref="Target"/> — URL як надіслано (без декодування).</summary>
/// <param name="Method">Метод.</param>
/// <param name="Target">Шлях із query, як у рядку запиту HTTP.</param>
/// <param name="Authorization">Заголовок <c>Authorization</c>.</param>
/// <param name="Accept">Заголовок <c>Accept</c>.</param>
/// <param name="Body">Тіло.</param>
public sealed record ODataRequest(string Method, string Target, string? Authorization, string? Accept, string Body);

/// <summary>
/// In-process мок OData-сервісу на випадковому порту: <c>/odata/$metadata</c> (CSDL з тестів, лише з
/// <c>Authorization</c> — як у 1С), entity sets з тестовими даними; кожен запит запам'ятовується.
/// </summary>
public sealed class MockODataApi : IAsyncDisposable
{
    /// <summary>Ключ контрагента в тестових даних.</summary>
    public const string CustomerKey = "6f1c2a8e-0d5b-4c33-9a5e-2b7d8f1e4a10";

    /// <summary>Назви властивостей як у CSDL (без camelCase).</summary>
    private static readonly JsonSerializerOptions Json = new();

    private readonly WebApplication _app;

    private MockODataApi(WebApplication app) => _app = app;

    /// <summary>Запити до сервісу (крім <c>$metadata</c>).</summary>
    public ConcurrentQueue<ODataRequest> Requests { get; } = new();

    /// <summary>Якщо задано — кожна відповідь entity sets має цей статус і тіло.</summary>
    public (int Status, string Body)? FailWith { get; set; }

    /// <summary>Базова адреса сервісу з кінцевим «/».</summary>
    public Uri BaseUrl => new(_app.Urls.Single() + "/odata/");

    public static async Task<MockODataApi> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var mock = new MockODataApi(app);
        app.Run(async ctx =>
        {
            var target = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget;
            if (target.StartsWith("/odata/$metadata", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(ctx.Request.Headers.Authorization))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                ctx.Response.ContentType = "application/xml";
                await ctx.Response.SendFileAsync(Path.Combine(AppContext.BaseDirectory, "OData", "trade.xml"));
                return;
            }

            using var reader = new StreamReader(ctx.Request.Body);
            mock.Requests.Enqueue(new ODataRequest(
                ctx.Request.Method, target, ctx.Request.Headers.Authorization.FirstOrDefault(), ctx.Request.Headers.Accept.FirstOrDefault(),
                await reader.ReadToEndAsync()));
            if (mock.FailWith is { } fail)
            {
                ctx.Response.StatusCode = fail.Status;
                await ctx.Response.WriteAsync(fail.Body);
                return;
            }

            var path = ctx.Request.Path.Value!;
            switch (ctx.Request.Method)
            {
                case "GET" when path == "/odata/Catalog_Контрагенты":
                    await ctx.Response.WriteAsJsonAsync<object>(new Dictionary<string, object>
                    {
                        ["@odata.context"] = "$metadata#Catalog_Контрагенты",
                        ["value"] = new[] { new { Ref_Key = CustomerKey, Description = "ТОВ Агро", Code = "000000001" } },
                    }, Json);
                    break;
                case "GET" when path == $"/odata/Catalog_Контрагенты({CustomerKey})":
                    await ctx.Response.WriteAsJsonAsync<object>(new { Ref_Key = CustomerKey, Description = "ТОВ Агро" }, Json);
                    break;
                case "POST" when path == "/odata/Document_Orders":
                    ctx.Response.StatusCode = StatusCodes.Status201Created;
                    await ctx.Response.WriteAsJsonAsync<object>(new { Ref_Key = Guid.Empty, Number = "ЗК-000123" }, Json);
                    break;
                case "PATCH" when path.StartsWith("/odata/OrderLines(", StringComparison.Ordinal):
                    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                    break;
                default:
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    await ctx.Response.WriteAsJsonAsync<object>(new { error = new { code = "404", message = $"{path} not found" } }, Json);
                    break;
            }
        });
        await app.StartAsync();
        return mock;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

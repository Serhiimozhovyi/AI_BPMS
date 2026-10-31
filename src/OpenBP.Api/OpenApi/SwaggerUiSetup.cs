using Swashbuckle.AspNetCore.SwaggerUI;

namespace OpenBP.Api.OpenApi;

/// <summary>
/// Swagger UI (F10-01): інтерфейс для документа <c>/openapi/v1.json</c> (F4-12) за адресою <c>/swagger</c>.
/// Сторінка відкрита без входу — у ній лише описи; «Try it out» іде з сесією браузера (cookie того самого походження)
/// або з API-ключем, введеним в «Authorize». Ключ не зберігається в <c>localStorage</c>.
/// </summary>
public static class SwaggerUiSetup
{
    /// <summary>Шлях сторінки (без скісної риски).</summary>
    public const string RoutePrefix = "swagger";

    /// <summary>Заголовок вкладки браузера.</summary>
    public const string Title = "AI_BPMS · REST API";

    /// <summary>Адреса документа OpenAPI, який показує Swagger UI.</summary>
    public static readonly string DocumentUrl = $"/openapi/{OpenApiSetup.DocumentName}.json";

    /// <summary>Підключає статичну сторінку Swagger UI на <c>/swagger</c>.</summary>
    /// <param name="app">Конвеєр запитів.</param>
    public static IApplicationBuilder UseOpenBpSwaggerUi(this IApplicationBuilder app) =>
        app.UseSwaggerUI(Configure);

    /// <summary>Налаштування сторінки (окремо — для тестів).</summary>
    /// <param name="o">Параметри Swagger UI.</param>
    public static void Configure(SwaggerUIOptions o)
    {
        o.RoutePrefix = RoutePrefix;
        o.DocumentTitle = Title;
        o.SwaggerEndpoint(DocumentUrl, "AI_BPMS REST API v1");
        // Розділи згорнуті: документ великий, людина розгортає потрібний.
        o.DocExpansion(DocExpansion.None);
        o.DefaultModelsExpandDepth(-1);
        o.DisplayRequestDuration();
        o.EnableFilter();
        o.EnableDeepLinking();
        // Секрет «Authorize» живе лише у вкладці: без EnablePersistAuthorization (localStorage).
    }
}

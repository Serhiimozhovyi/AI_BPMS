using Microsoft.AspNetCore.Cors.Infrastructure;

namespace OpenBP.Api;

/// <summary>
/// CORS для вбудовування редактора на сторінки інших застосунків (F4-13, docs/embedding.md): дозволені origin-и —
/// <c>OPENBP_CORS_ORIGINS</c> через кому (напр. <c>https://kit.example.com</c>). Без змінної крос-доменні запити
/// браузера не дозволені; запити того самого origin працюють як і раніше.
/// </summary>
public static class CorsSetup
{
    /// <summary>Змінна з дозволеними origin-ами.</summary>
    public const string OriginsKey = "OPENBP_CORS_ORIGINS";

    /// <summary>Розбирає список origin-ів: через кому, без пробілів і <c>/</c> в кінці; порожні відкидаються.</summary>
    /// <param name="value">Значення змінної.</param>
    public static string[] ParseOrigins(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => o.TrimEnd('/'))
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Політика за замовчуванням з конфігурації (читається при першому запиті): будь-які заголовки й методи,
    /// cookie (<c>AllowCredentials</c>), <c>Last-Event-ID</c> для SSE; preflight кешується на годину.
    /// </summary>
    /// <param name="services">Контейнер.</param>
    public static IServiceCollection AddOpenBpCors(this IServiceCollection services)
    {
        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            var origins = ParseOrigins(configuration[OriginsKey]);
            if (origins.Length == 0)
            {
                return;
            }

            options.AddDefaultPolicy(p => p
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .WithExposedHeaders("Location")
                .SetPreflightMaxAge(TimeSpan.FromHours(1)));
        });
        return services;
    }
}

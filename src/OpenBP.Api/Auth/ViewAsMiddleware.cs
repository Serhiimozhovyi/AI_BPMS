using OpenBP.Application.Auth;

namespace OpenBP.Api.Auth;

/// <summary>
/// Перегляд «як користувач» (F12-01, docs/architecture.md §10.6): запит сесії браузера із заголовком
/// <c>X-OpenBP-View-As</c> виконується від імені обраної ролі проєкту — principal підмінюється до сервісів, тож REST, SSE
/// і сторінки застосунку перевіряють права так само, як для справжнього учасника. Режим лише для читання: будь-який
/// запит, крім GET/HEAD/OPTIONS і виходу, — 403 <c>VIEW_AS_READ_ONLY</c>. З API-ключем чи bearer-токеном — 400.
/// </summary>
public sealed class ViewAsMiddleware(RequestDelegate next)
{
    private const string LogoutPath = "/api/v1/auth/logout";

    /// <summary>Обробляє запит.</summary>
    /// <param name="http">Контекст запиту.</param>
    /// <param name="service">Перевірка прав на перегляд.</param>
    public async Task InvokeAsync(HttpContext http, IViewAsService service)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(service);
        var header = http.Request.Headers[ViewAsRequest.Header].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            await next(http);
            return;
        }

        if (http.Request.Path.Equals(LogoutPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        if (http.User.Identity?.IsAuthenticated != true)
        {
            // Без сесії заголовок нічого не дає: 401 віддасть сам сервіс.
            await next(http);
            return;
        }

        if (http.Request.Headers.Authorization.Count > 0)
        {
            throw new ViewAsInvalidException("Перегляд «як користувач» — лише для сесії в браузері, не для API-ключа чи токена.");
        }

        var request = ViewAsRequest.Parse(header);
        var (context, userId, email) = await service.ResolveAsync(request, http.RequestAborted);
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
        {
            throw new ViewAsReadOnlyException();
        }

        http.User = OpenBpClaims.CreateViewAsPrincipal(context, userId, email, http.User.Identity.AuthenticationType);
        await next(http);
    }
}

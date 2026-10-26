using System.Text.Json.Nodes;

namespace OpenBP.Application.Webhooks;

/// <summary>
/// Вхід тригера <c>core.webhook</c> (docs/connectors.md §6, F2-10): маршрут <c>/webhook/{path}</c> поза <c>/api/v1</c>
/// викликає цей сервіс. Ролей не перевіряє — webhook анонімний, опційна автентифікація задається параметром
/// <c>auth</c> вузла (header-токен / Basic, секрет — у credential).
/// </summary>
public interface IWebhookService
{
    /// <summary>
    /// Знаходить зареєстрований webhook за шляхом, перевіряє метод і автентифікацію та ставить виконання опублікованої
    /// версії сценарію в чергу (<c>IExecutionLauncher</c>, режим <c>trigger</c>) з item'ом запиту
    /// <c>{ headers, query, body }</c> як виходом вузла-тригера. Відповідь — за параметром <c>respond</c> (F6-05):
    /// 202 з <c>executionId</c>, вихід останнього вузла після завершення або відповідь вузла <c>core.respondToWebhook</c>
    /// (не дочекались за <c>responseTimeoutSec</c> — 504). З ключем ідемпотентності повтор у вікні повертає ту саму
    /// відповідь без нового виконання.
    /// </summary>
    /// <param name="request">Вхідний HTTP-запит.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Відповідь для викликача.</returns>
    /// <exception cref="Errors.NotFoundException">Шлях не зареєстровано (невідомий або сценарій неактивний) чи метод інший.</exception>
    /// <exception cref="Errors.UnauthenticatedException">Автентифікація webhook не пройдена.</exception>
    Task<WebhookReply> TriggerAsync(WebhookRequest request, CancellationToken ct);
}

/// <summary>HTTP-запит до <c>/webhook/{path}</c>, уже розібраний хостом.</summary>
/// <param name="Method">HTTP-метод (<c>POST</c>, <c>GET</c>…).</param>
/// <param name="Path">Шлях після <c>/webhook/</c>; початкові й кінцеві <c>/</c> ігноруються.</param>
/// <param name="Headers">Заголовки: ім'я → значення (кілька значень — через <c>", "</c>); ім'я без урахування регістру.</param>
/// <param name="Query">Параметри рядка запиту: ім'я → значення.</param>
/// <param name="Body">Тіло: JSON, об'єкт полів форми, рядок або <c>null</c>.</param>
public sealed record WebhookRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string[]> Query,
    JsonNode? Body);

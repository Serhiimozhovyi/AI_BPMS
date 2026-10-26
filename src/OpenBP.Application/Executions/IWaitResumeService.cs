using System.Text.Json.Nodes;

namespace OpenBP.Application.Executions;

/// <summary>
/// Зовнішнє пробудження виконання, що чекає у вузлі <c>core.wait</c> v3 у режимі <c>webhook</c>/<c>form</c> (F6-01):
/// <c>POST /webhook/resume/{token}</c> (і форма F6-03) викликають цей сервіс. Ролей не перевіряє — токен і є секретом.
/// </summary>
public interface IWaitResumeService
{
    /// <summary>
    /// Гасить одноразовий токен і будить виконання: job пробудження (поставлений воркером при призупиненні) отримує
    /// <paramref name="signal"/> у <c>signals[nodeId]</c> і <c>run_at = now</c>; вузол віддає дані сигналу на вихід <c>main</c>.
    /// </summary>
    /// <param name="token">Токен з адреси пробудження.</param>
    /// <param name="signal">Дані пробудження (тіло запиту); <c>null</c> — порожнє тіло.</param>
    /// <param name="kind">
    /// Режим, якого чекає адреса (<see cref="ResumeTokens.WebhookKind"/> для <c>/webhook/resume</c>, <see cref="ResumeTokens.FormKind"/>
    /// для сторінки форми): токен іншого режиму — як невідомий; <c>null</c> — будь-який.
    /// </param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Зведення виконання після пробудження (стан <c>waiting</c>, доки воркер не візьме job).</returns>
    /// <exception cref="Errors.NotFoundException">Такого токена немає (чужий, вигаданий, іншого режиму або виконання вже видалено).</exception>
    /// <exception cref="Errors.ConflictException">
    /// <c>RESUME_TOKEN_USED</c> — токен уже використано; <c>EXECUTION_NOT_WAITING</c> — виконання не в стані <c>waiting</c>
    /// (ще виконується, завершене чи скасоване); <c>NODE_NOT_WAITING</c> — чекає інший вузол або строк цього вже вийшов.
    /// </exception>
    Task<ExecutionSummary> ResumeAsync(string token, JsonNode? signal, string? kind, CancellationToken ct);
}

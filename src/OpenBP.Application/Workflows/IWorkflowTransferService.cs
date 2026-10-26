using OpenBP.Application.Auth;
using OpenBP.Application.Errors;

namespace OpenBP.Application.Workflows;

/// <summary>
/// Перенесення сценаріїв між середовищами (F6-09, docs/workflow-format.md §«Експорт та імпорт»): експорт з маніфестом
/// залежностей і імпорт з перемапінгом <c>serverId</c>/<c>credentialId</c>/<c>errorWorkflowId</c>. Обидві дії —
/// <see cref="UserRole.Editor"/>.
/// </summary>
public interface IWorkflowTransferService
{
    /// <summary>Поточна версія сценарію з маніфестом залежностей.</summary>
    /// <param name="id">Сценарій.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    Task<WorkflowExport> ExportAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Створює новий неактивний сценарій із перемапленими посиланнями. Відповідник шукається так: явний
    /// <see cref="WorkflowImportRequest.Mapping"/> → єдиний об'єкт приймача з тією самою назвою (без урахування регістру)
    /// і транспортом/типом з маніфесту → посилання без маніфесту лишається, якщо такий id у приймачі є.
    /// </summary>
    /// <param name="request">Визначення, маніфест, відповідники.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ImportUnmappedException">Частина посилань без відповідника (нічого не створено).</exception>
    /// <exception cref="WorkflowValidationException">Перемаплений сценарій не проходить валідацію.</exception>
    Task<WorkflowImportResult> ImportAsync(WorkflowImportRequest request, CancellationToken ct);

    /// <summary>
    /// Перетворює експорт сценарію n8n на тіло для <see cref="ImportAsync"/> (F6-24, docs/workflow-format.md
    /// §«Імпорт з n8n»): нічого не зберігає. Вузли без відповідника стають заглушками, про них — зауваження.
    /// </summary>
    /// <param name="workflow">JSON сценарію n8n.</param>
    /// <exception cref="ArgumentException">Це не сценарій n8n.</exception>
    N8nConversion ConvertN8n(System.Text.Json.Nodes.JsonObject workflow);
}

using System.Text.Json.Nodes;
using OpenBP.Application.Errors;

namespace OpenBP.Application.Projects;

/// <summary>
/// Сторінки застосунку проєкту (F8-04, docs/projects.md §3–4): збереження з перевіркою і показ даних. Сторінку бачать
/// admin, editor проєкту й учасники, чия роль застосунку є в її <c>roles</c> (порожній список — усі учасники); іншим вона
/// не існує (404). Змінює сторінки editor проєкту. Фільтр сторінки (з <c>$user</c>) застосовується на сервері до кожного
/// читання й запису і не знімається параметрами запиту. REST, UI і MCP викликають цей сервіс.
/// </summary>
public interface IProjectPageService
{
    /// <summary>Видимі користувачу сторінки проєкту в порядку меню.</summary>
    /// <exception cref="NotFoundException">Проєкту немає або користувач не його учасник.</exception>
    Task<IReadOnlyList<ProjectPageDto>> ListAsync(Guid projectId, CancellationToken ct);

    /// <summary>Сторінка для показу: визначення, колонки з типами, схема форми.</summary>
    /// <exception cref="NotFoundException">Сторінки немає або вона не для ролі користувача.</exception>
    Task<ProjectPageView> GetAsync(Guid projectId, string key, CancellationToken ct);

    /// <summary>Створює або замінює сторінку (editor проєкту).</summary>
    /// <exception cref="UnprocessableException">Невідома таблиця, колонка чи сценарій, сценарій іншого проєкту, некоректне визначення.</exception>
    Task<ProjectPageDto> UpsertAsync(Guid projectId, string key, ProjectPageInput input, CancellationToken ct);

    /// <summary>Видаляє сторінку (editor проєкту).</summary>
    Task DeleteAsync(Guid projectId, string key, CancellationToken ct);

    /// <summary>Рядки сторінки-списку з фільтром сторінки, пошуком, сортуванням і пагінацією.</summary>
    Task<PageRows> RowsAsync(Guid projectId, string key, PageRowsQuery query, CancellationToken ct);

    /// <summary>Рядок для картки (з пов'язаними списками) чи форми зміни; рядок поза фільтром сторінки — 404.</summary>
    Task<PageRecord> RowAsync(Guid projectId, string key, string rowKey, CancellationToken ct);

    /// <summary>Надсилання форми: <c>insert</c>, <c>upsert</c> чи <c>update</c> рядка <paramref name="rowKey"/>.</summary>
    /// <exception cref="Projects.PageFormInvalidException">422 з помилками за полями (обов'язкові, тип, унікальність).</exception>
    Task<PageSubmitResult> SubmitAsync(Guid projectId, string key, JsonObject values, string? rowKey, CancellationToken ct);

    /// <summary>Серверна дія над рядком (<c>operation: delete</c>, editor проєкту таблиці).</summary>
    Task RunActionAsync(Guid projectId, string key, string rowKey, int action, CancellationToken ct);
}

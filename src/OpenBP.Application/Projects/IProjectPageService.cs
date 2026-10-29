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

    /// <summary>
    /// Дані чернетки сторінки без збереження (editor проєкту): та сама перевірка, що й у <see cref="UpsertAsync"/>, і ті самі
    /// запити даних, що й у сторінки, — перші рядки, показники, події тижня, блоки головної.
    /// </summary>
    /// <exception cref="UnprocessableException">Некоректне визначення — як у <see cref="UpsertAsync"/>.</exception>
    Task<PagePreview> PreviewAsync(Guid projectId, string key, ProjectPageInput input, CancellationToken ct);

    /// <summary>Видаляє сторінку (editor проєкту).</summary>
    Task DeleteAsync(Guid projectId, string key, CancellationToken ct);

    /// <summary>Рядки сторінки-списку з фільтром сторінки, пошуком, сортуванням і пагінацією.</summary>
    Task<PageRows> RowsAsync(Guid projectId, string key, PageRowsQuery query, CancellationToken ct);

    /// <summary>Рядок для картки (з пов'язаними списками) чи форми зміни; рядок поза фільтром сторінки — 404.</summary>
    Task<PageRecord> RowAsync(Guid projectId, string key, string rowKey, CancellationToken ct);

    /// <summary>
    /// Надсилання форми: <c>insert</c>, <c>upsert</c> чи <c>update</c> рядка <paramref name="rowKey"/>, або запуск сценарію
    /// (<c>onSubmit.workflow</c>) зі значеннями; відмова сценарію (<c>ok: false</c>) — помилка форми.
    /// </summary>
    /// <exception cref="Projects.PageFormInvalidException">422 з помилками за полями (обов'язкові, тип, унікальність).</exception>
    Task<PageSubmitResult> SubmitAsync(Guid projectId, string key, JsonObject values, string? rowKey, CancellationToken ct);

    /// <summary>Показники сторінки <c>metrics</c>: плитки й діаграми по таблицях проєкту.</summary>
    Task<PageMetrics> MetricsAsync(Guid projectId, string key, CancellationToken ct);

    /// <summary>
    /// Серверна дія над рядком: <c>operation: delete</c> (editor проєкту таблиці) або запуск сценарію проєкту з
    /// <c>{ row, rowKey, user }</c> з очікуванням результату до 30 с (F8-05).
    /// </summary>
    Task<PageActionResult> RunActionAsync(Guid projectId, string key, string rowKey, int action, CancellationToken ct);

    /// <summary>
    /// Підлеглий список <c>expand[index]</c> під рядком списку (F8-15): рядки пов'язаної таблиці, де <c>by</c> = ключ рядка,
    /// з доступними діями; рядок поза фільтром сторінки — 404.
    /// </summary>
    Task<PageRelatedRows> ExpandAsync(Guid projectId, string key, string rowKey, int index, CancellationToken ct);

    /// <summary>Дія над рядком підлеглого списку: рядок <paramref name="subKey"/> має посилатися на <paramref name="rowKey"/>.</summary>
    Task<PageActionResult> RunExpandActionAsync(Guid projectId, string key, string rowKey, int index, string subKey, int action, CancellationToken ct);

    /// <summary>
    /// Події сторінки <c>calendar</c> (F8-15) за діапазоном <c>[from, to)</c> до 62 днів, з фільтром сторінки, фільтрами
    /// користувача й правами.
    /// </summary>
    Task<PageCalendarEvents> CalendarAsync(Guid projectId, string key, DateTimeOffset from, DateTimeOffset to, IReadOnlyDictionary<string, PageFilterInput>? filters, CancellationToken ct);

    /// <summary>
    /// Дані блоків сторінки <c>dashboard</c> (F8-15): плитки, перші рядки списків, події за сьогодні чи тиждень, форми,
    /// тексти, доступні кнопки. Спільні фільтри сторінки діють на блоки, таблиця яких має таку колонку.
    /// </summary>
    Task<PageDashboard> DashboardAsync(Guid projectId, string key, IReadOnlyDictionary<string, PageFilterInput>? filters, CancellationToken ct);

    /// <summary>Кнопка блоку <c>actions</c>: сценарій проєкту з item <c>{ user }</c> і очікуванням результату до 30 с.</summary>
    Task<PageActionResult> RunBlockActionAsync(Guid projectId, string key, int block, int action, CancellationToken ct);
}

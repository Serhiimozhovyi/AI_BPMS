using System.Text.Json.Nodes;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Workflows;

namespace OpenBP.Application.Projects;

/// <summary>Тіло <c>POST /projects/import</c> і MCP <c>import_project</c> (F8-06).</summary>
/// <param name="Bundle">Шаблон проєкту з <see cref="IProjectTransferService.ExportAsync"/>.</param>
/// <param name="Mapping">Явні відповідники конекторів і credentials (як у F6-09); не вказане шукається за назвою й типом.</param>
/// <param name="ProjectId">Наявний проєкт, який оновлюється; <c>null</c> — створити новий.</param>
/// <param name="Name">Назва нового проєкту замість назви з шаблону.</param>
public sealed record ProjectImportRequest(JsonObject? Bundle, ImportMapping? Mapping = null, Guid? ProjectId = null, string? Name = null);

/// <summary>Що зробив імпорт шаблону проєкту.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="Name">Назва проєкту.</param>
/// <param name="ProjectCreated">Проєкт створено цим імпортом.</param>
/// <param name="TablesCreated">Нові таблиці.</param>
/// <param name="TablesExtended">Наявні таблиці, до яких додано колонки.</param>
/// <param name="RowsInserted">Скільки рядків вставлено в нові таблиці (за назвою таблиці).</param>
/// <param name="PagesCreated">Нові сторінки (ключі).</param>
/// <param name="PagesUpdated">Замінені сторінки (ключі).</param>
/// <param name="WorkflowsCreated">Нові сценарії (назви).</param>
/// <param name="WorkflowsUpdated">Сценарії, чернетку яких замінено (назви).</param>
/// <param name="MissingVariables">Ключі <c>$vars</c>, яких немає в цьому середовищі.</param>
/// <param name="Mapping">Застосовані відповідники конекторів і credentials.</param>
public sealed record ProjectImportResult(
    Guid ProjectId,
    string Name,
    bool ProjectCreated,
    IReadOnlyList<string> TablesCreated,
    IReadOnlyList<string> TablesExtended,
    IReadOnlyDictionary<string, int> RowsInserted,
    IReadOnlyList<string> PagesCreated,
    IReadOnlyList<string> PagesUpdated,
    IReadOnlyList<string> WorkflowsCreated,
    IReadOnlyList<string> WorkflowsUpdated,
    IReadOnlyList<string> MissingVariables,
    ImportMapping Mapping);

/// <summary>
/// Шаблон проєкту (F8-06, docs/projects.md §5): експорт проєкту одним JSON (властивості, ролі застосунку, таблиці,
/// сторінки, сценарії з маніфестом залежностей) та імпорт у порожній чи наявний проєкт. Експортує editor проєкту; імпорт у
/// новий проєкт — глобальний editor, у наявний — editor проєкту. REST, UI, CLI і MCP викликають цей сервіс.
/// </summary>
public interface IProjectTransferService
{
    /// <summary>Поточний формат шаблону.</summary>
    const int FormatVersion = 1;

    /// <summary>
    /// Шаблон проєкту. Рядки (<c>rows</c>) — лише таблиць з <paramref name="rowsFor"/>, без чутливих колонок. Секретів,
    /// учасників і значень змінних у шаблоні немає.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="rowsFor">Назви таблиць, рядки яких додати; <c>null</c> — жодної.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Проєкту немає або користувач не його учасник.</exception>
    Task<JsonObject> ExportAsync(Guid projectId, IReadOnlyCollection<string>? rowsFor, CancellationToken ct);

    /// <summary>
    /// Створює проєкт (чи оновлює наявний) за шаблоном. Відповідники конекторів і credentials перевіряються до першої
    /// зміни. Повторний імпорт у наявний проєкт замінює сторінки за ключем і чернетки сценаріїв за назвою, таблиці лише
    /// розширює новими колонками (рядки вставляються лише в нові таблиці). Сценарії лишаються неактивними.
    /// </summary>
    /// <param name="request">Шаблон і параметри.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="UnprocessableException">Шаблон некоректний (<c>BUNDLE_INVALID</c>, <c>BUNDLE_VERSION_UNSUPPORTED</c>).</exception>
    /// <exception cref="ImportUnmappedException">Частина конекторів чи credentials без відповідника (нічого не створено).</exception>
    Task<ProjectImportResult> ImportAsync(ProjectImportRequest request, CancellationToken ct);
}

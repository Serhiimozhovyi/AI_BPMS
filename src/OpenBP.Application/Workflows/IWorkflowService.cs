using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>
/// Сценарії і їхні версії (docs/architecture.md §10.1–10.2). Один сервіс для REST (<c>/workflows</c>),
/// UI і MCP-tools <c>list_workflows</c>/<c>get_workflow</c>/<c>create_workflow</c>/<c>update_workflow</c>/<c>validate_workflow</c>.
/// Реалізація — F2-06.
/// <list type="bullet">
/// <item><c>workflows.name</c> — джерело правди для назви: при збереженні <see cref="WorkflowDefinition.Name"/>
/// перезаписується з нього; перейменування — <see cref="RenameAsync"/>.</item>
/// <item>Кожне <see cref="SaveAsync"/> — нова незмінна версія; активний сценарій виконує поточну версію.</item>
/// <item>Ролі: читання — <see cref="UserRole.Viewer"/>, зміни — <see cref="UserRole.Editor"/>.</item>
/// </list>
/// </summary>
public interface IWorkflowService
{
    /// <summary>Сторінка списку, сортування за <c>updated_at desc</c>.</summary>
    /// <param name="filter">Фільтр.</param>
    /// <param name="page">Пагінація.</param>
    /// <param name="ct">Скасування.</param>
    Task<PageResult<WorkflowSummary>> ListAsync(WorkflowFilter filter, PageRequest page, CancellationToken ct);

    /// <summary>Сценарій з визначенням поточної версії.</summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    Task<WorkflowDetail> GetAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Створює сценарій з версією 1. Назва — <see cref="WorkflowDefinition.Name"/>. Визначення валідується як
    /// неактивне (правила 1–3, 5–6 <c>workflow-format.md</c>).
    /// </summary>
    /// <param name="definition">Визначення.</param>
    /// <param name="groupId">Група нового сценарію (F3-63); <c>null</c> — без групи.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="WorkflowValidationException">Визначення некоректне; нічого не створено.</exception>
    /// <exception cref="NotFoundException">Групи <paramref name="groupId"/> немає.</exception>
    Task<WorkflowDetail> CreateAsync(WorkflowDefinition definition, Guid? groupId, CancellationToken ct);

    /// <summary>
    /// Зберігає визначення як нову версію і робить її чернеткою (поточною, F6-10). Тригери активного сценарію не
    /// змінюються — вони виконують опубліковану версію (<see cref="PublishAsync"/>); чернетку перевіряють як неактивну.
    /// Помилка валідації — версія не створюється.
    /// </summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="definition">Визначення; його <c>name</c> ігнорується.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    /// <exception cref="WorkflowValidationException">Визначення некоректне; нічого не збережено.</exception>
    Task<WorkflowDetail> SaveAsync(Guid id, WorkflowDefinition definition, CancellationToken ct);

    /// <summary>
    /// План збереження (F3-116, режим «пропозиція»): що зміниться відносно поточної версії — вузли, зв'язки, налаштування —
    /// і помилки валідації. Нічого не записує; помилки валідації — у плані, не винятком.
    /// </summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="definition">Визначення; його <c>name</c> ігнорується.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    Task<WorkflowPlan> PlanSaveAsync(Guid id, WorkflowDefinition definition, CancellationToken ct);

    /// <summary>Зберігає визначення за планом <see cref="PlanSaveAsync"/>, як <see cref="SaveAsync"/>.</summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="definition">Те саме визначення, що й для плану.</param>
    /// <param name="planId"><see cref="WorkflowPlan.PlanId"/>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    /// <exception cref="ConflictException">План застарів або визначення інше (<c>WORKFLOW_PLAN_STALE</c>); нічого не збережено.</exception>
    /// <exception cref="WorkflowValidationException">Визначення некоректне; нічого не збережено.</exception>
    Task<WorkflowDetail> ApplySaveAsync(Guid id, WorkflowDefinition definition, string planId, CancellationToken ct);

    /// <summary>
    /// Публікує чернетку (F6-10): перевіряє її з <c>IsActive = true</c> і робить опублікованою; в активного сценарію
    /// тригери (<c>webhooks</c>, Quartz) перереєструються з неї в тій самій транзакції. Неактивний сценарій не вмикає.
    /// Виконання, що вже йдуть або чекають, довиконуються на своїй версії. Ідемпотентно.
    /// </summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    /// <exception cref="WorkflowValidationException">Чернетка не може працювати; опублікована версія і тригери не змінені.</exception>
    Task<WorkflowDetail> PublishAsync(Guid id, CancellationToken ct);

    /// <summary>«Скасувати зміни» (F6-10): чернеткою знову стає опублікована версія; історія версій не змінюється.</summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    /// <exception cref="ConflictException">Сценарій ще не публікувався (код <c>WORKFLOW_NOT_PUBLISHED</c>).</exception>
    Task<WorkflowDetail> DiscardDraftAsync(Guid id, CancellationToken ct);

    /// <summary>Перейменовує сценарій без нової версії.</summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="name">Нова назва, непорожня.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    Task<WorkflowDetail> RenameAsync(Guid id, string name, CancellationToken ct);

    /// <summary>Переносить сценарій у групу або прибирає з групи (F3-63) без нової версії.</summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="groupId">Група; <c>null</c> — без групи.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію або групи немає.</exception>
    Task<WorkflowDetail> MoveAsync(Guid id, Guid? groupId, CancellationToken ct);

    /// <summary>Перевіряє визначення без збереження (MCP <c>validate_workflow</c>); помилки — у результаті, не винятком.</summary>
    /// <param name="definition">Визначення.</param>
    /// <param name="asActive">Перевіряти як активний сценарій (правило 4 — потрібен тригер).</param>
    /// <param name="ct">Скасування.</param>
    Task<WorkflowValidationResult> ValidateAsync(WorkflowDefinition definition, bool asActive, CancellationToken ct);

    /// <summary>
    /// Активує сценарій: валідація опублікованої версії з <c>IsActive = true</c>, реєстрація її тригерів (<c>webhooks</c>, Quartz).
    /// Сценарій, що ще не публікувався, публікує чернетку. Ідемпотентно.
    /// </summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    /// <exception cref="WorkflowValidationException">Поточна версія не може бути активною (напр. немає тригера або конфлікт шляху webhook).</exception>
    Task<WorkflowDetail> ActivateAsync(Guid id, CancellationToken ct);

    /// <summary>Деактивує сценарій і знімає тригери. Ідемпотентно; виконання, що вже йдуть, не зупиняє.</summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    Task<WorkflowDetail> DeactivateAsync(Guid id, CancellationToken ct);

    /// <summary>Видаляє неактивний сценарій з версіями, webhook-ами, <c>node_state</c>, виконаннями і кроками.</summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    /// <exception cref="ConflictException">Сценарій активний (код <c>WORKFLOW_ACTIVE</c>).</exception>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Версії сценарію, нові першими.</summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    Task<IReadOnlyList<WorkflowVersionSummary>> ListVersionsAsync(Guid id, CancellationToken ct);

    /// <summary>Визначення конкретної версії.</summary>
    /// <param name="id"><c>workflows.id</c>.</param>
    /// <param name="version">Номер версії.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію або версії немає.</exception>
    Task<WorkflowDefinition> GetVersionAsync(Guid id, int version, CancellationToken ct);
}

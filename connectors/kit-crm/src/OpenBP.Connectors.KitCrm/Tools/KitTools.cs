using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.KitCrm.Tools;

/// <summary>
/// Tools «Кіт CRM» поверх OData Кіта (docs/kit-sales.md §5.3): угоди, задачі, контрагенти, особи, нотатки, керівник
/// менеджера і <c>changes_since</c> для <c>mcp.poll</c>. Ключі — Guid; м'яко видалені записи (<c>IsDelete</c>) не
/// повертаються ніколи, бо GET Кіта їх сам не відсікає. Описи — англійською (їх читають ШІ-агенти).
/// <see cref="ConnectorException"/> → <c>isError</c>.
/// </summary>
/// <param name="kit">Клієнт OData Кіта.</param>
/// <param name="dictionaries">Довідники й користувачі з кешем.</param>
/// <param name="options">Налаштування.</param>
/// <param name="time">Годинник.</param>
[McpServerToolType]
public sealed class KitTools(KitClient kit, KitDictionaries dictionaries, IOptions<ConnectorOptions> options, TimeProvider time)
{
    private const string NotDeleted = "IsDelete eq false";

    private int MaxTop => options.Value.MaxTop;

    // ---------- угоди ----------

    /// <summary><c>find_sales</c>.</summary>
    /// <param name="query">Частина назви.</param>
    /// <param name="stageId">Стадія.</param>
    /// <param name="responsibleId">Відповідальний.</param>
    /// <param name="contractorId">Контрагент.</param>
    /// <param name="updatedSince">Змінені після.</param>
    /// <param name="top">Скільки.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "find_sales", Title = "Find deals", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Searches deals (CrmSales) by part of the name, stage, responsible person, contractor or change time; newest changes first. Deleted deals are never returned.")]
    public Task<ListResult<KitSale>> FindSales(
        [Description("Part of the deal name (case-insensitive).")] string? query = null,
        [Description("Stage id (CrmSaleStages, see list_sale_stages).")] string? stageId = null,
        [Description("Responsible person id (CrmPerson).")] string? responsibleId = null,
        [Description("Contractor id.")] string? contractorId = null,
        [Description("Only deals changed after this ISO 8601 time, e.g. 2026-10-24T08:00:00Z.")] string? updatedSince = null,
        [Description("Maximum number of deals (default 20).")] int top = 20,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var filter = new List<string> { NotDeleted };
            if (!string.IsNullOrWhiteSpace(query))
            {
                filter.Add($"contains(Name,{Text(query)})");
            }

            AddId(filter, "CrmSaleStageId", stageId, nameof(stageId));
            AddId(filter, "CrmPersonId", responsibleId, nameof(responsibleId));
            AddId(filter, "CrmContractorId", contractorId, nameof(contractorId));
            if (updatedSince is not null)
            {
                filter.Add($"Updated gt {Instant(updatedSince, nameof(updatedSince))}");
            }

            var rows = await ListAsync("CrmSales", filter, "Updated desc", top, ct).ConfigureAwait(false);
            return new ListResult<KitSale>([.. rows.Select(ToSale)]);
        });

    /// <summary><c>get_sale</c>.</summary>
    /// <param name="id">Угода.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_sale", Title = "Get deal", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Returns one deal (CrmSales) by id: name, stage, responsible person, contractor, sums and dates.")]
    public Task<KitSale> GetSale([Description("Deal id (Guid).")] string id, CancellationToken ct = default) =>
        Call(async () => ToSale(await GetOneAsync("CrmSales", id, "угоду", ct).ConfigureAwait(false)));

    /// <summary><c>update_sale</c>.</summary>
    /// <param name="id">Угода.</param>
    /// <param name="fields">Поля.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "update_sale", Title = "Update deal fields", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Changes fields of a deal (PATCH CrmSales): pass Kit field names, e.g. { \"SaleSum\": 120000, \"Description\": \"…\", \"DealForecastDate\": \"2026-11-30T00:00:00Z\" }. Id, IsDelete, Created and Updated cannot be changed. Returns the deal after the change.")]
    public Task<KitSale> UpdateSale(
        [Description("Deal id (Guid).")] string id,
        [Description("Fields to change, Kit names (PascalCase).")] Dictionary<string, JsonElement> fields,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (fields is not { Count: > 0 })
            {
                throw new ConnectorException("fields — хоча б одне поле, напр. { \"SaleSum\": 1000 }.");
            }

            var forbidden = fields.Keys.FirstOrDefault(k => k.ToUpperInvariant() is "ID" or "ISDELETE" or "CREATED" or "UPDATED" or "CREATEDBY" or "UPDATEDBY");
            if (forbidden is not null)
            {
                throw new ConnectorException($"Поле «{forbidden}» змінювати не можна. Видалення угоди — лише в Кіті.");
            }

            return await PatchSaleAsync(id, fields.ToDictionary(p => p.Key, p => (object?)p.Value), ct).ConfigureAwait(false);
        });

    /// <summary><c>set_sale_stage</c>.</summary>
    /// <param name="id">Угода.</param>
    /// <param name="stageId">Стадія.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "set_sale_stage", Title = "Set deal stage", UseStructuredContent = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Moves a deal to another stage (CrmSaleStageId). Stage ids — list_sale_stages. Returns the deal after the change.")]
    public Task<KitSale> SetSaleStage(
        [Description("Deal id (Guid).")] string id,
        [Description("Stage id (Guid) from list_sale_stages.")] string stageId,
        CancellationToken ct = default) =>
        Call(() => PatchSaleAsync(id, new Dictionary<string, object?> { ["CrmSaleStageId"] = Id(stageId, nameof(stageId)) }, ct));

    /// <summary><c>list_sale_stages</c>.</summary>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_sale_stages", Title = "List deal stages", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Lists deal stages (CrmSaleStages): id and name.")]
    public Task<ListResult<KitDictionaryItem>> ListSaleStages(CancellationToken ct = default) =>
        Call(async () => new ListResult<KitDictionaryItem>(await dictionaries.SaleStagesAsync(ct).ConfigureAwait(false)));

    /// <summary><c>add_sale_note</c>.</summary>
    /// <param name="saleId">Угода.</param>
    /// <param name="text">Текст.</param>
    /// <param name="authorPersonId">Автор.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "add_sale_note", Title = "Add note to deal", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Adds a note to a deal (CrmSaleNotes). The author is authorPersonId or the connector's default author (Connector:AuthorPersonId).")]
    public Task<CreatedNote> AddSaleNote(
        [Description("Deal id (Guid).")] string saleId,
        [Description("Note text.")] string text,
        [Description("Author person id (CrmPerson); omit to use the connector's default author.")] string? authorPersonId = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ConnectorException("text — текст нотатки.");
            }

            var sale = Id(saleId, nameof(saleId));
            var author = authorPersonId ?? options.Value.AuthorPersonId
                ?? throw new ConnectorException("Не задано автора нотатки: передайте authorPersonId або задайте Connector__AuthorPersonId (особа сервісного користувача в Кіті).");
            var id = Guid.NewGuid().ToString();
            var body = new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["Name"] = Shorten(text.Trim(), 100),
                ["Description"] = text.Trim(),
                ["PersonCreatedId"] = Id(author, nameof(authorPersonId)),
                ["CrmSaleId"] = sale,
                ["IsDelete"] = false,
            };
            await kit.SendAsync(HttpMethod.Post, "CrmSaleNotes", body, ct).ConfigureAwait(false);
            return new CreatedNote(id, sale);
        });

    // ---------- задачі ----------

    /// <summary><c>create_task</c>.</summary>
    /// <param name="title">Заголовок.</param>
    /// <param name="responsibleId">Відповідальний.</param>
    /// <param name="deadline">Дедлайн.</param>
    /// <param name="description">Опис.</param>
    /// <param name="managerId">Менеджер.</param>
    /// <param name="categoryId">Категорія.</param>
    /// <param name="saleId">Угода.</param>
    /// <param name="contractorId">Контрагент.</param>
    /// <param name="personIds">Учасники.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "create_task", Title = "Create CRM task", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Creates a CRM task (CrmTasks) in status Планове, optionally linked to a deal, contractor and participants. Kit itself notifies the responsible person and the manager (bell and email). Returns the created task.")]
    public Task<KitTask> CreateTask(
        [Description("Task title.")] string title,
        [Description("Responsible person id (CrmPerson), e.g. the deal's responsibleId.")] string responsibleId,
        [Description("Deadline, ISO 8601, e.g. 2026-10-27T18:00:00+02:00.")] string deadline,
        [Description("Task description.")] string? description = null,
        [Description("Manager (controller) person id (CrmPerson).")] string? managerId = null,
        [Description("Task category id (CrmTaskCategories).")] string? categoryId = null,
        [Description("Deal id to link the task to.")] string? saleId = null,
        [Description("Contractor id.")] string? contractorId = null,
        [Description("Participant person ids (CrmPerson).")] string[]? personIds = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                throw new ConnectorException("title — заголовок задачі.");
            }

            var id = Guid.NewGuid().ToString();
            var body = new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["Name"] = title.Trim(),
                ["Description"] = description,
                ["ResponsiblePersonId"] = Id(responsibleId, nameof(responsibleId)),
                ["ManagerId"] = OptionalId(managerId, nameof(managerId)),
                ["CrmTaskCategoryId"] = OptionalId(categoryId, nameof(categoryId)),
                ["CrmContractorId"] = OptionalId(contractorId, nameof(contractorId)),
                ["CrmTaskStatusId"] = KitDictionaries.Planned,
                ["StartDate"] = Iso(time.GetUtcNow()),
                ["FinishDate"] = Iso(Parse(deadline, nameof(deadline))),
                ["IsDelete"] = false,
            };
            if (OptionalId(saleId, nameof(saleId)) is { } sale)
            {
                body["CrmSaleCrmTaskCross"] = new[] { new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString(), ["CrmSaleId"] = sale, ["CrmTaskId"] = id } };
            }

            if (personIds is { Length: > 0 })
            {
                body["CrmPersonCrmTaskCross"] = personIds
                    .Select(p => new Dictionary<string, object?> { ["Id"] = Guid.NewGuid().ToString(), ["CrmPersonId"] = Id(p, nameof(personIds)), ["CrmTaskId"] = id })
                    .ToArray();
            }

            var created = await kit.SendAsync(HttpMethod.Post, "CrmTasks", body, ct).ConfigureAwait(false);
            return created is { ValueKind: JsonValueKind.Object } row
                ? await ToTaskAsync(row, ct).ConfigureAwait(false)
                : await ToTaskAsync(await GetOneAsync("CrmTasks", id, "задачу", ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        });

    /// <summary><c>get_task</c>.</summary>
    /// <param name="id">Задача.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_task", Title = "Get CRM task", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Returns one CRM task by id with its status name (Планове, В роботі, Перенесена, Виконане, Невиконане, Протерміноване) and done = true for Виконане/Невиконане. Протерміноване is set by Kit itself for overdue tasks and does not mean the task is finished.")]
    public Task<KitTask> GetTask([Description("Task id (Guid).")] string id, CancellationToken ct = default) =>
        Call(async () => await ToTaskAsync(await GetOneAsync("CrmTasks", id, "задачу", ct).ConfigureAwait(false), ct).ConfigureAwait(false));

    /// <summary><c>list_tasks</c>.</summary>
    /// <param name="saleId">Угода.</param>
    /// <param name="responsibleId">Відповідальний.</param>
    /// <param name="status">Статус.</param>
    /// <param name="deadlineBefore">Дедлайн до.</param>
    /// <param name="top">Скільки.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_tasks", Title = "List CRM tasks", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Lists CRM tasks by deal, responsible person, status (name or id) and deadline; earliest deadline first.")]
    public Task<ListResult<KitTask>> ListTasks(
        [Description("Only tasks linked to this deal.")] string? saleId = null,
        [Description("Responsible person id (CrmPerson).")] string? responsibleId = null,
        [Description("Status name (e.g. Протерміноване) or id.")] string? status = null,
        [Description("Only tasks with deadline before this ISO 8601 time.")] string? deadlineBefore = null,
        [Description("Maximum number of tasks (default 50).")] int top = 50,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var filter = new List<string> { NotDeleted };
            if (OptionalId(saleId, nameof(saleId)) is { } sale)
            {
                var links = await kit.GetAsync($"CrmSaleCrmTaskCross?$filter={Uri.EscapeDataString($"CrmSaleId eq {sale}")}&$select=CrmTaskId", ct).ConfigureAwait(false);
                var ids = Values(links).Select(l => Str(l, "CrmTaskId")).OfType<string>().Distinct().ToList();
                if (ids.Count == 0)
                {
                    return new ListResult<KitTask>([]);
                }

                filter.Add("(" + string.Join(" or ", ids.Select(i => $"Id eq {i}")) + ")");
            }

            AddId(filter, "ResponsiblePersonId", responsibleId, nameof(responsibleId));
            if (!string.IsNullOrWhiteSpace(status))
            {
                filter.Add($"CrmTaskStatusId eq {await dictionaries.TaskStatusIdAsync(status, ct).ConfigureAwait(false)}");
            }

            if (deadlineBefore is not null)
            {
                filter.Add($"FinishDate lt {Instant(deadlineBefore, nameof(deadlineBefore))}");
            }

            var rows = await ListAsync("CrmTasks", filter, "FinishDate asc", top, ct).ConfigureAwait(false);
            var tasks = new List<KitTask>(rows.Count);
            foreach (var row in rows)
            {
                tasks.Add(await ToTaskAsync(row, ct).ConfigureAwait(false));
            }

            return new ListResult<KitTask>(tasks);
        });

    /// <summary><c>set_task_status</c>.</summary>
    /// <param name="id">Задача.</param>
    /// <param name="status">Статус.</param>
    /// <param name="result">Результат.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "set_task_status", Title = "Set CRM task status", UseStructuredContent = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Changes a CRM task's status (name, e.g. Виконане, or id) and optionally its result text. Kit notifies the responsible person and the manager. Returns the task after the change.")]
    public Task<KitTask> SetTaskStatus(
        [Description("Task id (Guid).")] string id,
        [Description("Status name or id.")] string status,
        [Description("Result text to save on the task.")] string? result = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var key = Id(id, nameof(id));
            var body = new Dictionary<string, object?> { ["CrmTaskStatusId"] = await dictionaries.TaskStatusIdAsync(status, ct).ConfigureAwait(false) };
            if (result is not null)
            {
                body["Result"] = result;
            }

            await GetOneAsync("CrmTasks", key, "задачу", ct).ConfigureAwait(false);
            await kit.SendAsync(HttpMethod.Patch, $"CrmTasks?key={key}", body, ct).ConfigureAwait(false);
            return await ToTaskAsync(await GetOneAsync("CrmTasks", key, "задачу", ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        });

    // ---------- контрагенти й особи ----------

    /// <summary><c>find_contractors</c>.</summary>
    /// <param name="query">Назва або ЄДРПОУ.</param>
    /// <param name="top">Скільки.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "find_contractors", Title = "Find contractors", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Searches contractors (CrmContractors) by part of the name or full name, or by exact ЄДРПОУ/ІПН.")]
    public Task<ListResult<KitContractor>> FindContractors(
        [Description("Part of the name, or ЄДРПОУ/ІПН.")] string query,
        [Description("Maximum number (default 20).")] int top = 20,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var q = Required(query, nameof(query));
            var filter = new List<string> { NotDeleted, $"(contains(Name,{Text(q)}) or contains(FullName,{Text(q)}) or Edrpou eq {Text(q)} or Inn eq {Text(q)})" };
            var rows = await ListAsync("CrmContractors", filter, "Name asc", top, ct).ConfigureAwait(false);
            return new ListResult<KitContractor>([.. rows.Select(ToContractor)]);
        });

    /// <summary><c>get_contractor</c>.</summary>
    /// <param name="id">Контрагент.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_contractor", Title = "Get contractor", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Returns one contractor by id.")]
    public Task<KitContractor> GetContractor([Description("Contractor id (Guid).")] string id, CancellationToken ct = default) =>
        Call(async () => ToContractor(await GetOneAsync("CrmContractors", id, "контрагента", ct).ConfigureAwait(false)));

    /// <summary><c>find_persons</c>.</summary>
    /// <param name="query">Ім'я.</param>
    /// <param name="top">Скільки.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "find_persons", Title = "Find persons", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Searches persons (CrmPersons: clients' contacts and our employees) by part of the name or last name.")]
    public Task<ListResult<KitPerson>> FindPersons(
        [Description("Part of the name or last name.")] string query,
        [Description("Maximum number (default 20).")] int top = 20,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var q = Required(query, nameof(query));
            var filter = new List<string> { NotDeleted, $"(contains(Name,{Text(q)}) or contains(LastName,{Text(q)}))" };
            var rows = await ListAsync("CrmPersons", filter, "Name asc", top, ct).ConfigureAwait(false);
            return new ListResult<KitPerson>([.. rows.Select(ToPerson)]);
        });

    /// <summary><c>get_person</c>.</summary>
    /// <param name="id">Особа.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_person", Title = "Get person", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Returns one person (CrmPerson) by id, with managerId when the person is an employee with a manager in the profile.")]
    public Task<KitPerson> GetPerson([Description("Person id (Guid).")] string id, CancellationToken ct = default) =>
        Call(async () => ToPerson(await GetOneAsync("CrmPersons", id, "особу", ct).ConfigureAwait(false)));

    /// <summary><c>get_person_user</c>.</summary>
    /// <param name="crmPersonId">Особа.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_person_user", Title = "Get user of person", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Returns the Kit user (name, email) linked to a person (Users.CrmPersonId) — e.g. to email the responsible manager. found = false when the person has no active user.")]
    public Task<UserResult> GetPersonUser([Description("Person id (CrmPerson).")] string crmPersonId, CancellationToken ct = default) =>
        Call(async () =>
        {
            var user = await dictionaries.UserByPersonAsync(Id(crmPersonId, nameof(crmPersonId)), ct).ConfigureAwait(false);
            return new UserResult(user is not null, user);
        });

    /// <summary><c>get_user_manager</c>.</summary>
    /// <param name="crmPersonId">Особа.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_user_manager", Title = "Get manager of employee", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Returns the manager of an employee from the employee's Kit profile (CrmPerson.ManagerId) with the manager's name and email (from Users). found = false when no manager is set — then use a fallback address.")]
    public Task<ManagerResult> GetUserManager([Description("Employee person id (CrmPerson), e.g. a task's responsibleId.")] string crmPersonId, CancellationToken ct = default) =>
        Call(async () =>
        {
            var person = ToPerson(await GetOneAsync("CrmPersons", crmPersonId, "особу", ct).ConfigureAwait(false));
            if (person.ManagerId is not { } managerId)
            {
                return new ManagerResult(false, null);
            }

            var user = await dictionaries.UserByPersonAsync(managerId, ct).ConfigureAwait(false);
            if (user is not null)
            {
                return new ManagerResult(true, user);
            }

            var manager = await kit.GetAsync($"CrmPersons/{managerId}", ct).ConfigureAwait(false);
            return manager is { } row && !IsDeleted(row)
                ? new ManagerResult(true, new KitUser(managerId, Str(row, "Name"), null))
                : new ManagerResult(false, null);
        });

    // ---------- зміни для mcp.poll ----------

    /// <summary><c>changes_since</c>.</summary>
    /// <param name="entity">Сутність.</param>
    /// <param name="since">Після.</param>
    /// <param name="withinMinutes">За останні хвилини.</param>
    /// <param name="createdOnly">Лише нові.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "changes_since", Title = "Changes since", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Records of an entity (sales, tasks, contractors, persons) created or changed after `since` (ISO 8601) or within the last `withinMinutes`, oldest first, in the same shape as get_sale/get_task/... For mcp.poll use itemsPath = result and keyField = id; with createdOnly = true only new records are returned. cursor is the latest change time among them.")]
    public Task<ChangesResult> ChangesSince(
        [Description("sales | tasks | contractors | persons.")] string entity,
        [Description("ISO 8601 time; changes strictly after it.")] string? since = null,
        [Description("Look-back window in minutes when since is omitted (1–10080, default 60).")] int withinMinutes = 60,
        [Description("Only records created (not just changed) in the window.")] bool createdOnly = false,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            DateTimeOffset from;
            if (since is not null)
            {
                from = Parse(since, nameof(since));
            }
            else if (withinMinutes is >= 1 and <= 10080)
            {
                from = time.GetUtcNow().AddMinutes(-withinMinutes);
            }
            else
            {
                throw new ConnectorException("withinMinutes — від 1 до 10080 (тиждень).");
            }

            var changes = await ChangesAsync(entity, from, createdOnly, ct).ConfigureAwait(false);
            var cursor = changes.Select(c => c.Changed).Append(from).Max();
            return new ChangesResult([.. changes.Select(c => c.Record)], Iso(cursor));
        });

    /// <summary>
    /// Записи сутності, створені чи змінені після <paramref name="from"/>, від найстаріших, з часом зміни кожного —
    /// спільне для <c>changes_since</c> і стрічки <c>kit://changes/{entity}</c> (<see cref="Events.KitEvents"/>).
    /// </summary>
    /// <param name="entity"><c>sales</c>, <c>tasks</c>, <c>contractors</c> або <c>persons</c>.</param>
    /// <param name="from">Зміни строго після цього часу.</param>
    /// <param name="createdOnly">Лише нові записи.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConnectorException">Невідома сутність або помилка Кіта.</exception>
    public async Task<IReadOnlyList<(object Record, DateTimeOffset Changed)>> ChangesAsync(string? entity, DateTimeOffset from, bool createdOnly, CancellationToken ct)
    {
        var set = EntitySet(entity);
        var at = Iso(from);
        var filter = new List<string> { NotDeleted, createdOnly ? $"Created gt {at}" : $"(Updated gt {at} or Created gt {at})" };
        var rows = await ListAsync(set, filter, createdOnly ? "Created asc" : "Updated asc", MaxTop, ct).ConfigureAwait(false);
        var result = new List<(object, DateTimeOffset)>(rows.Count);
        foreach (var row in rows)
        {
            var changed = DateTimeOffset.MinValue;
            foreach (var field in new[] { "Updated", "Created" })
            {
                if (Str(row, field) is { } value && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value2) && value2 > changed)
                {
                    changed = value2;
                }
            }

            result.Add((set switch
            {
                "CrmSales" => ToSale(row),
                "CrmTasks" => await ToTaskAsync(row, ct).ConfigureAwait(false),
                "CrmContractors" => ToContractor(row),
                _ => ToPerson(row),
            }, changed));
        }

        return result;
    }

    /// <summary>Набір OData для сутності <c>changes_since</c>.</summary>
    /// <param name="entity"><c>sales</c>, <c>tasks</c>, <c>contractors</c> або <c>persons</c>.</param>
    /// <exception cref="ConnectorException">Невідома сутність.</exception>
    public static string EntitySet(string? entity) => entity?.Trim().ToLowerInvariant() switch
    {
        "sales" => "CrmSales",
        "tasks" => "CrmTasks",
        "contractors" => "CrmContractors",
        "persons" => "CrmPersons",
        _ => throw new ConnectorException($"entity «{entity}» — одне з: sales, tasks, contractors, persons."),
    };

    // ---------- допоміжне ----------

    private async Task<KitSale> PatchSaleAsync(string id, Dictionary<string, object?> body, CancellationToken ct)
    {
        var key = Id(id, nameof(id));
        // PATCH неіснуючого ключа Кіт приймає мовчки (204) — спершу перевіряємо, що угода є.
        await GetOneAsync("CrmSales", key, "угоду", ct).ConfigureAwait(false);
        await kit.SendAsync(HttpMethod.Patch, $"CrmSales?key={key}", body, ct).ConfigureAwait(false);
        return ToSale(await GetOneAsync("CrmSales", key, "угоду", ct).ConfigureAwait(false));
    }

    private async Task<JsonElement> GetOneAsync(string set, string id, string what, CancellationToken ct)
    {
        var key = Id(id, "id");
        var row = await kit.GetAsync($"{set}/{key}", ct).ConfigureAwait(false);
        return row is { ValueKind: JsonValueKind.Object } found && !IsDeleted(found)
            ? found
            : throw new ConnectorException($"Не знайдено {what} з id {key} (або її видалено в Кіті).");
    }

    private async Task<List<JsonElement>> ListAsync(string set, List<string> filter, string orderBy, int top, CancellationToken ct)
    {
        var take = Math.Clamp(top, 1, MaxTop);
        var path = $"{set}?$filter={Uri.EscapeDataString(string.Join(" and ", filter))}&$orderby={Uri.EscapeDataString(orderBy)}&$top={take}";
        // Фільтр IsDelete уже в запиті; повторна перевірка — на випадок сервера, що його проігнорував.
        return [.. Values(await kit.GetAsync(path, ct).ConfigureAwait(false)).Where(r => !IsDeleted(r))];
    }

    private async Task<KitTask> ToTaskAsync(JsonElement row, CancellationToken ct)
    {
        var statusId = Str(row, "CrmTaskStatusId");
        var status = statusId is null ? null : await dictionaries.TaskStatusNameAsync(statusId, ct).ConfigureAwait(false);
        return new KitTask(
            Str(row, "Id") ?? "",
            Str(row, "Name"),
            Str(row, "Description"),
            Str(row, "Result"),
            status,
            statusId,
            KitDictionaries.IsFinished(statusId),
            Str(row, "ResponsiblePersonId"),
            Str(row, "ManagerId"),
            Str(row, "CrmTaskCategoryId"),
            Str(row, "CrmContractorId"),
            Str(row, "FinishDate"),
            Str(row, "Created"),
            Str(row, "Updated"));
    }

    private static KitSale ToSale(JsonElement row) => new(
        Str(row, "Id") ?? "",
        Str(row, "Name"),
        Str(row, "Description"),
        Str(row, "CrmSaleStageId"),
        Str(row, "CrmPersonId"),
        Str(row, "CrmContractorId"),
        Decimal(row, "SaleSum"),
        Decimal(row, "SaleBudgetForecast"),
        Str(row, "Currency"),
        Str(row, "StartDate"),
        Str(row, "FinishDate"),
        Str(row, "Created"),
        Str(row, "Updated"));

    private static KitPerson ToPerson(JsonElement row) => new(
        Str(row, "Id") ?? "",
        Str(row, "Name"),
        Str(row, "FirstName"),
        Str(row, "LastName"),
        Str(row, "CrmContractorId"),
        Str(row, "ManagerId"));

    private static KitContractor ToContractor(JsonElement row) => new(
        Str(row, "Id") ?? "",
        Str(row, "Name"),
        Str(row, "FullName"),
        Str(row, "Edrpou"),
        Str(row, "Inn"));

    /// <summary>Елементи <c>value</c> відповіді-колекції OData.</summary>
    internal static IEnumerable<JsonElement> Values(JsonElement? response) =>
        response is { ValueKind: JsonValueKind.Object } r && Prop(r, "value") is { ValueKind: JsonValueKind.Array } items
            ? items.EnumerateArray()
            : [];

    internal static bool IsDeleted(JsonElement row) => Prop(row, "IsDelete") is { ValueKind: JsonValueKind.True };

    /// <summary>Значення поля PascalCase або camelCase (Кіт віддає PascalCase, але приймає обидва).</summary>
    internal static JsonElement? Prop(JsonElement row, string name)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (row.TryGetProperty(name, out var value))
        {
            return value;
        }

        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        return row.TryGetProperty(camel, out value) ? value : null;
    }

    internal static string? Str(JsonElement row, string name) => Prop(row, name) switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } v => v.GetRawText(),
        _ => null,
    };

    private static decimal? Decimal(JsonElement row, string name) =>
        Prop(row, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var d) ? d : null;

    private static void AddId(List<string> filter, string field, string? value, string name)
    {
        if (OptionalId(value, name) is { } id)
        {
            filter.Add($"{field} eq {id}");
        }
    }

    /// <summary>Guid у канонічному вигляді — він же безпечний для підстановки в <c>$filter</c>.</summary>
    private static string Id(string? value, string name) =>
        Guid.TryParse(value?.Trim(), out var id)
            ? id.ToString()
            : throw new ConnectorException($"{name} — id запису Кіта (Guid), а передано «{value}».");

    private static string? OptionalId(string? value, string name) => string.IsNullOrWhiteSpace(value) ? null : Id(value, name);

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ConnectorException($"{name} — непорожній рядок.") : value.Trim();

    /// <summary>Рядковий літерал OData: апостроф подвоюється.</summary>
    private static string Text(string value) => "'" + value.Trim().Replace("'", "''", StringComparison.Ordinal) + "'";

    private static DateTimeOffset Parse(string value, string name) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : throw new ConnectorException($"{name} — дата ISO 8601, напр. 2026-10-27T18:00:00+02:00, а передано «{value}».");

    private static string Instant(string value, string name) => Iso(Parse(value, name));

    private static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ConnectorException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}

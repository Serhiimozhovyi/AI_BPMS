using System.Text.Json.Nodes;
using OpenBP.Application.Errors;
using OpenBP.Application.Forms;
using OpenBP.Engine.Data;

namespace OpenBP.Application.Projects;

/// <summary>Типи сторінок застосунку (docs/projects.md §3).</summary>
public static class ProjectPageKinds
{
    /// <summary>Список рядків таблиці.</summary>
    public const string List = "list";

    /// <summary>Картка рядка з пов'язаними списками.</summary>
    public const string Detail = "detail";

    /// <summary>Форма створення чи зміни рядка.</summary>
    public const string Form = "form";

    /// <summary>Підтримувані типи.</summary>
    public static readonly IReadOnlyList<string> All = [List, Detail, Form];
}

/// <summary>Колонка списку чи поле картки.</summary>
/// <param name="Column">Колонка таблиці.</param>
/// <param name="Label">Підпис; <c>null</c> — назва колонки.</param>
/// <param name="Format">Підказка показу: <c>datetime</c>, <c>date</c>, <c>number</c>…</param>
/// <param name="Display">Для колонки <c>ref</c>: <c>&lt;таблиця&gt;.&lt;колонка&gt;</c>, значення якої показати замість ключа.</param>
public sealed record PageColumn(string Column, string? Label = null, string? Format = null, string? Display = null);

/// <summary>Умова сторінки: як <see cref="DataFilter"/>, значення може бути виразом з <c>$user</c>.</summary>
/// <param name="Column">Колонка.</param>
/// <param name="Operator">Оператор (<see cref="DataFilterOperators"/>).</param>
/// <param name="Value">Значення або вираз <c>{{ $user.id }}</c>.</param>
public sealed record PageFilter(string Column, string Operator, JsonNode? Value = null);

/// <summary>Сортування.</summary>
/// <param name="Column">Колонка.</param>
/// <param name="Direction"><c>asc</c> або <c>desc</c>.</param>
public sealed record PageSort(string Column, string? Direction = "asc");

/// <summary>
/// Дія над рядком: рівно одне з <see cref="Workflow"/> (F8-05), <see cref="Page"/> (перехід) чи <see cref="Operation"/>
/// (<c>delete</c>, лише editor проєкту).
/// </summary>
/// <param name="Label">Підпис кнопки.</param>
public sealed record PageAction(string Label)
{
    /// <summary>Ключ (назва) або id сценарію проєкту; запуск — з F8-05.</summary>
    public string? Workflow { get; init; }

    /// <summary>Ключ сторінки, яку відкрити з ключем рядка.</summary>
    public string? Page { get; init; }

    /// <summary><c>delete</c> — видалити рядок.</summary>
    public string? Operation { get; init; }

    /// <summary>Питання підтвердження перед дією.</summary>
    public string? Confirm { get; init; }

    /// <summary>Вираз з <c>$row</c> і <c>$user</c>: дію показано, лише якщо він істинний.</summary>
    public string? When { get; init; }
}

/// <summary>Посилання на іншу сторінку застосунку.</summary>
/// <param name="Page">Ключ сторінки.</param>
public sealed record PageLink(string Page);

/// <summary>Пов'язаний список на картці: рядки <see cref="Table"/>, де колонка <see cref="By"/> дорівнює ключу рядка картки.</summary>
/// <param name="Table">Таблиця.</param>
/// <param name="By">Колонка, що посилається на рядок картки.</param>
public sealed record PageRelated(string Table, string By)
{
    /// <summary>Підпис секції.</summary>
    public string? Label { get; init; }

    /// <summary>Колонки.</summary>
    public IReadOnlyList<PageColumn>? Columns { get; init; }

    /// <summary>Дії над рядками (лише <c>page</c>).</summary>
    public IReadOnlyList<PageAction>? Actions { get; init; }
}

/// <summary>Поле картки (<c>detail</c>) чи форми (<c>form</c>).</summary>
/// <param name="Column">Колонка.</param>
public sealed record PageField(string Column)
{
    /// <summary>Підпис.</summary>
    public string? Label { get; init; }

    /// <summary>Підказка показу (картка).</summary>
    public string? Format { get; init; }

    /// <summary>Обов'язкове на цій формі (крім обов'язкових колонок таблиці).</summary>
    public bool Required { get; init; }

    /// <summary>Підказка під полем.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Фіксоване значення або вираз з <c>$user</c> (<c>{{ $user.id }}</c>): поле не показується, значення ставить сервер.
    /// </summary>
    public JsonNode? Value { get; init; }

    /// <summary>Для колонки <c>ref</c>: <c>&lt;таблиця&gt;.&lt;колонка&gt;</c> — що показати замість ключа (і підписи варіантів у формі).</summary>
    public string? Display { get; init; }
}

/// <summary>Що робить форма: рівно одне з <see cref="Operation"/> чи <see cref="Workflow"/> (F8-05).</summary>
public sealed record PageSubmit
{
    /// <summary><c>insert</c>, <c>upsert</c> або <c>update</c> (рядок з адреси форми).</summary>
    public string? Operation { get; init; }

    /// <summary>Сценарій, якому передати значення (F8-05).</summary>
    public string? Workflow { get; init; }
}

/// <summary>
/// Визначення сторінки (<c>project_pages.definition</c>); які поля потрібні — залежить від типу (docs/projects.md §3).
/// <see cref="Filter"/> діє на сервері в усіх типах: рядок поза фільтром для сторінки не існує.
/// </summary>
public sealed record PageDefinition
{
    /// <summary>Таблиця проєкту або спільна (за назвою).</summary>
    public string Table { get; init; } = "";

    /// <summary>Колонки списку (<c>list</c>).</summary>
    public IReadOnlyList<PageColumn>? Columns { get; init; }

    /// <summary>Умови, які не можна зняти з клієнта.</summary>
    public IReadOnlyList<PageFilter>? Filter { get; init; }

    /// <summary>Колонки пошуку (<c>list</c>).</summary>
    public IReadOnlyList<string>? Search { get; init; }

    /// <summary>Сортування за замовчуванням (<c>list</c>).</summary>
    public PageSort? Sort { get; init; }

    /// <summary>Рядків на сторінці списку, 1–200 (типово 50).</summary>
    public int? PageSize { get; init; }

    /// <summary>Дії над рядком (<c>list</c>, <c>detail</c>).</summary>
    public IReadOnlyList<PageAction>? Actions { get; init; }

    /// <summary>Кнопка «Створити» у списку — сторінка-форма.</summary>
    public PageLink? Create { get; init; }

    /// <summary>Поля картки (<c>detail</c>) чи форми (<c>form</c>).</summary>
    public IReadOnlyList<PageField>? Fields { get; init; }

    /// <summary>Пов'язані списки картки.</summary>
    public IReadOnlyList<PageRelated>? Related { get; init; }

    /// <summary>Що робить форма.</summary>
    public PageSubmit? OnSubmit { get; init; }

    /// <summary>Куди перейти після форми.</summary>
    public PageLink? Then { get; init; }
}

/// <summary>Сторінка застосунку.</summary>
/// <param name="Id"><c>project_pages.id</c>.</param>
/// <param name="Key">Ключ — адреса <c>/projects/:id/app/&lt;key&gt;</c>.</param>
/// <param name="Name">Назва в меню застосунку.</param>
/// <param name="Kind"><see cref="ProjectPageKinds"/>.</param>
/// <param name="Definition">Визначення.</param>
/// <param name="Roles">Ролі застосунку, яким видно сторінку; порожньо — усім учасникам.</param>
/// <param name="SortOrder">Порядок у меню.</param>
/// <param name="UpdatedAt">Остання зміна.</param>
public sealed record ProjectPageDto(
    Guid Id, string Key, string Name, string Kind, PageDefinition Definition, IReadOnlyList<string> Roles, int SortOrder, DateTimeOffset UpdatedAt);

/// <summary>Сторінка для збереження (<c>PUT /projects/{id}/pages/{key}</c>, MCP <c>upsert_project_page</c>).</summary>
/// <param name="Name">Назва.</param>
/// <param name="Kind">Тип.</param>
/// <param name="Definition">Визначення.</param>
/// <param name="Roles">Ролі застосунку.</param>
/// <param name="SortOrder">Порядок; <c>null</c> — у кінці (нова) чи без змін.</param>
public sealed record ProjectPageInput(string Name, string Kind, PageDefinition Definition, IReadOnlyList<string>? Roles = null, int? SortOrder = null);

/// <summary>Колонка таблиці сторінки для показу.</summary>
/// <param name="Column">Назва колонки.</param>
/// <param name="Label">Підпис.</param>
/// <param name="Type">Тип колонки (<c>string</c>, <c>datetime</c>…).</param>
/// <param name="Format">Підказка показу.</param>
public sealed record PageViewColumn(string Column, string Label, string Type, string? Format);

/// <summary>Сторінка для показу (<c>GET /projects/{id}/pages/{key}</c>).</summary>
/// <param name="Page">Сторінка.</param>
/// <param name="TableId">Таблиця сторінки.</param>
/// <param name="Columns">Колонки списку чи поля картки з типами.</param>
/// <param name="Schema">JSON Schema форми (<c>form</c>) для рушія форм F6-03; для інших типів <c>null</c>.</param>
/// <param name="CanEdit">Чи може користувач змінювати сторінки проєкту (editor).</param>
public sealed record ProjectPageView(ProjectPageDto Page, Guid TableId, IReadOnlyList<PageViewColumn> Columns, JsonObject? Schema, bool CanEdit);

/// <summary>Рядок на сторінці.</summary>
/// <param name="Key">Ключ рядка.</param>
/// <param name="Data">Значення лише тих колонок, що є на сторінці (чутливі — <c>null</c> без права).</param>
/// <param name="Display">Показані значення колонок <c>ref</c> з <c>display</c>.</param>
/// <param name="Actions">Індекси доступних дій з <see cref="PageDefinition.Actions"/>.</param>
public sealed record PageRow(string Key, JsonObject Data, IReadOnlyDictionary<string, string?> Display, IReadOnlyList<int> Actions);

/// <summary>Рядки списку.</summary>
/// <param name="Items">Рядки.</param>
/// <param name="Page">Номер сторінки.</param>
/// <param name="PageSize">Розмір.</param>
/// <param name="Total">Усього за фільтром і пошуком.</param>
public sealed record PageRows(IReadOnlyList<PageRow> Items, int Page, int PageSize, int Total);

/// <summary>Пов'язаний список картки.</summary>
/// <param name="Label">Підпис.</param>
/// <param name="Columns">Колонки.</param>
/// <param name="Rows">Рядки (до 200).</param>
public sealed record PageRelatedRows(string Label, IReadOnlyList<PageViewColumn> Columns, IReadOnlyList<PageRow> Rows);

/// <summary>Картка рядка або значення для форми зміни.</summary>
/// <param name="Row">Рядок.</param>
/// <param name="Related">Пов'язані списки (<c>detail</c>).</param>
public sealed record PageRecord(PageRow Row, IReadOnlyList<PageRelatedRows> Related);

/// <summary>Запит параметрів списку.</summary>
/// <param name="Search">Підрядок пошуку.</param>
/// <param name="Sort">Колонка сортування (з колонок сторінки).</param>
/// <param name="Descending">Спадання.</param>
/// <param name="Page">Номер сторінки від 1.</param>
/// <param name="PageSize">Розмір; <c>null</c> — з визначення.</param>
public sealed record PageRowsQuery(string? Search = null, string? Sort = null, bool? Descending = null, int Page = 1, int? PageSize = null);

/// <summary>Результат форми.</summary>
/// <param name="Row">Збережений рядок.</param>
/// <param name="Then">Сторінка, куди перейти; <c>null</c> — лишитися.</param>
public sealed record PageSubmitResult(PageRow Row, string? Then);

/// <summary>422 <c>PAGE_FORM_INVALID</c>: значення форми не пройшли перевірку; <see cref="Errors"/> — за полями.</summary>
public sealed class PageFormInvalidException : OpenBpException
{
    /// <summary>Код помилки.</summary>
    public const string ErrorCode = "PAGE_FORM_INVALID";

    /// <param name="errors">Помилки за полями (<c>field</c> — колонка; порожній — уся форма).</param>
    public PageFormInvalidException(IReadOnlyList<FormFieldError> errors)
        : base(ErrorCode, 422, errors is { Count: 1 } ? errors[0].Message : $"Перевірте поля форми: {errors?.Count ?? 0} помилок.")
    {
        Errors = errors ?? [];
    }

    /// <summary>Помилки; у REST — поле <c>errors</c> problem-details.</summary>
    public IReadOnlyList<FormFieldError> Errors { get; }
}

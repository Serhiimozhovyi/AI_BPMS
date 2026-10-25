using System.Text.Json.Serialization;

namespace OpenBP.Connectors.KitCrm.Tools;

/// <summary>Угода <c>CrmSales</c>.</summary>
/// <param name="Id">Id угоди.</param>
/// <param name="Name">Назва.</param>
/// <param name="Description">Опис.</param>
/// <param name="StageId">Стадія (<c>CrmSaleStageId</c>).</param>
/// <param name="ResponsibleId">Відповідальний — <c>CrmPerson</c> (<c>CrmPersonId</c>).</param>
/// <param name="ContractorId">Контрагент.</param>
/// <param name="Sum">Сума угоди (<c>SaleSum</c>).</param>
/// <param name="Forecast">Прогноз бюджету (<c>SaleBudgetForecast</c>).</param>
/// <param name="Currency">Валюта.</param>
/// <param name="StartDate">Початок.</param>
/// <param name="FinishDate">Завершення.</param>
/// <param name="Created">Створено (UTC).</param>
/// <param name="Updated">Змінено (UTC).</param>
public sealed record KitSale(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("stageId")] string? StageId,
    [property: JsonPropertyName("responsibleId")] string? ResponsibleId,
    [property: JsonPropertyName("contractorId")] string? ContractorId,
    [property: JsonPropertyName("sum")] decimal? Sum,
    [property: JsonPropertyName("forecast")] decimal? Forecast,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("startDate")] string? StartDate,
    [property: JsonPropertyName("finishDate")] string? FinishDate,
    [property: JsonPropertyName("created")] string? Created,
    [property: JsonPropertyName("updated")] string? Updated);

/// <summary>Задача <c>CrmTasks</c>.</summary>
/// <param name="Id">Id задачі.</param>
/// <param name="Title">Заголовок (<c>Name</c>).</param>
/// <param name="Description">Опис.</param>
/// <param name="Result">Результат виконання.</param>
/// <param name="Status">Назва статусу (Планове, В роботі, Виконане…).</param>
/// <param name="StatusId">Id статусу.</param>
/// <param name="Done">Задачу завершено: статус Виконане або Невиконане.</param>
/// <param name="ResponsibleId">Відповідальний — <c>CrmPerson</c>.</param>
/// <param name="ManagerId">Менеджер (контроль) — <c>CrmPerson</c>.</param>
/// <param name="CategoryId">Категорія.</param>
/// <param name="ContractorId">Контрагент.</param>
/// <param name="Deadline">Дедлайн (<c>FinishDate</c>, UTC).</param>
/// <param name="Created">Створено (UTC).</param>
/// <param name="Updated">Змінено (UTC).</param>
public sealed record KitTask(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("statusId")] string? StatusId,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("responsibleId")] string? ResponsibleId,
    [property: JsonPropertyName("managerId")] string? ManagerId,
    [property: JsonPropertyName("categoryId")] string? CategoryId,
    [property: JsonPropertyName("contractorId")] string? ContractorId,
    [property: JsonPropertyName("deadline")] string? Deadline,
    [property: JsonPropertyName("created")] string? Created,
    [property: JsonPropertyName("updated")] string? Updated);

/// <summary>Особа <c>CrmPersons</c> — клієнт або співробітник.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Ім'я, як його показує Кіт (<c>Name</c>).</param>
/// <param name="FirstName">Ім'я.</param>
/// <param name="LastName">Прізвище.</param>
/// <param name="ContractorId">Контрагент.</param>
/// <param name="ManagerId">Керівник — <c>CrmPerson</c>.</param>
public sealed record KitPerson(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("contractorId")] string? ContractorId,
    [property: JsonPropertyName("managerId")] string? ManagerId);

/// <summary>Контрагент <c>CrmContractors</c>.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Коротка назва.</param>
/// <param name="FullName">Повна назва.</param>
/// <param name="Edrpou">ЄДРПОУ.</param>
/// <param name="Inn">ІПН.</param>
public sealed record KitContractor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("fullName")] string? FullName,
    [property: JsonPropertyName("edrpou")] string? Edrpou,
    [property: JsonPropertyName("inn")] string? Inn);

/// <summary>Довідникове значення (стадія угоди, статус чи категорія задачі).</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Назва.</param>
public sealed record KitDictionaryItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name);

/// <summary>Користувач Кіта, зв'язаний з особою (<c>Users.CrmPersonId</c>).</summary>
/// <param name="CrmPersonId">Особа.</param>
/// <param name="Name">Ім'я.</param>
/// <param name="Email">Email.</param>
public sealed record KitUser(
    [property: JsonPropertyName("crmPersonId")] string CrmPersonId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("email")] string? Email);

/// <summary>Результат <c>get_user_manager</c>.</summary>
/// <param name="Found">Керівника знайдено.</param>
/// <param name="Manager">Керівник або <c>null</c>.</param>
public sealed record ManagerResult(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("manager")] KitUser? Manager);

/// <summary>Результат <c>get_person_user</c>.</summary>
/// <param name="Found">Користувача знайдено.</param>
/// <param name="User">Користувач або <c>null</c>.</param>
public sealed record UserResult(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("user")] KitUser? User);

/// <summary>Створена нотатка.</summary>
/// <param name="Id">Id нотатки.</param>
/// <param name="SaleId">Угода.</param>
public sealed record CreatedNote(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("saleId")] string SaleId);

/// <summary>Колекція як <c>{ "result": [...] }</c>: <c>structuredContent</c> має бути об'єктом.</summary>
/// <typeparam name="T">Елемент.</typeparam>
/// <param name="Result">Елементи.</param>
public sealed record ListResult<T>([property: JsonPropertyName("result")] IReadOnlyList<T> Result);

/// <summary>Результат <c>changes_since</c>: записи і новий курсор.</summary>
/// <param name="Result">Змінені записи (JSON як у відповідних get_*).</param>
/// <param name="Cursor">Найбільший <c>Updated</c>/<c>Created</c> серед записів або вхідний <c>since</c>.</param>
public sealed record ChangesResult(
    [property: JsonPropertyName("result")] IReadOnlyList<object> Result,
    [property: JsonPropertyName("cursor")] string? Cursor);

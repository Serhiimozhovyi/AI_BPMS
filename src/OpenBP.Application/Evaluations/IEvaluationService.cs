using System.Text.Json.Nodes;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Evaluations;

/// <summary>Способи перевірки кейсу (<c>evaluation_cases.check</c>).</summary>
public static class EvaluationChecks
{
    /// <summary>Фактичне значення дорівнює очікуваному (структурно).</summary>
    public const string Exact = "exact";

    /// <summary>Значення за шляхом <c>config.path</c> (<c>text</c>, <c>data.items[0].name</c>) дорівнює очікуваному.</summary>
    public const string JsonPath = "jsonPath";

    /// <summary>Вираз <c>config.expression</c> (<c>$json</c> — фактичне, <c>$node["expected"].json</c> — очікуване) дає <c>true</c>.</summary>
    public const string Expression = "expression";

    /// <summary>Модель-суддя (<c>config.credentialId</c>, <c>config.model</c>, <c>config.criteria</c>) відповідає PASS.</summary>
    public const string Llm = "llm";

    /// <summary>Усі способи.</summary>
    public static IReadOnlyList<string> All { get; } = [Exact, JsonPath, Expression, Llm];
}

/// <summary>Кейс у запиті створення чи зміни оцінки.</summary>
/// <param name="Name">Назва; без неї — «Кейс N».</param>
/// <param name="Input">Items на вході 0 вузла (<c>[{ "json": { … } }]</c>).</param>
/// <param name="Expected">Очікуване значення (для <c>exact</c>, <c>jsonPath</c>; для <c>expression</c>/<c>llm</c> — необов'язково).</param>
/// <param name="Check">Спосіб перевірки, див. <see cref="EvaluationChecks"/>; без нього — <c>exact</c>.</param>
/// <param name="Config">Налаштування перевірки.</param>
public sealed record EvaluationCaseInput(string? Name, IReadOnlyList<Item>? Input, JsonNode? Expected, string? Check, JsonObject? Config);

/// <summary>Тіло <c>POST /workflows/{id}/evaluations</c>.</summary>
/// <param name="NodeId">Вузол поточної версії (зазвичай <c>ai.llm</c>/<c>ai.agent</c>).</param>
/// <param name="Name">Назва набору.</param>
/// <param name="Cases">Кейси.</param>
public sealed record EvaluationCreate(string NodeId, string? Name, IReadOnlyList<EvaluationCaseInput>? Cases);

/// <summary>Тіло <c>PUT /evaluations/{id}</c>: назва і повний список кейсів (замінює наявні).</summary>
/// <param name="Name">Назва; <c>null</c> — без змін.</param>
/// <param name="Cases">Кейси; <c>null</c> — без змін.</param>
public sealed record EvaluationUpdate(string? Name, IReadOnlyList<EvaluationCaseInput>? Cases);

/// <summary>Кейс оцінки.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Назва.</param>
/// <param name="Input">Items на вході 0.</param>
/// <param name="Expected">Очікуване.</param>
/// <param name="Check">Спосіб перевірки.</param>
/// <param name="Config">Налаштування перевірки.</param>
public sealed record EvaluationCase(Guid Id, string Name, IReadOnlyList<Item> Input, JsonNode? Expected, string Check, JsonObject Config);

/// <summary>Результат кейсу в прогоні.</summary>
/// <param name="CaseId">Кейс.</param>
/// <param name="Name">Назва кейсу на момент прогону.</param>
/// <param name="Passed">Збіг.</param>
/// <param name="Actual">Фактичне значення: <c>json</c> єдиного item'а виходу 0, масив <c>json</c>-ів для кількох, <c>null</c> — немає.</param>
/// <param name="Reason">Чому не збіглося (або пояснення судді).</param>
/// <param name="Error">Помилка вузла чи перевірки.</param>
/// <param name="DurationMs">Тривалість кроку вузла.</param>
/// <param name="Tokens">Токени моделі з підкроків вузла (<c>ai.agent</c>); <c>null</c> — невідомо.</param>
/// <param name="ExecutionId">Виконання кейсу.</param>
public sealed record EvaluationCaseResult(
    Guid CaseId,
    string Name,
    bool Passed,
    JsonNode? Actual,
    string? Reason,
    string? Error,
    long DurationMs,
    int? Tokens,
    Guid? ExecutionId);

/// <summary>Прогін оцінки.</summary>
/// <param name="Id">Id.</param>
/// <param name="EvaluationId">Оцінка.</param>
/// <param name="VersionId">Версія сценарію, на якій виконувались кейси.</param>
/// <param name="Version">Номер цієї версії.</param>
/// <param name="StartedAt">Початок.</param>
/// <param name="FinishedAt">Кінець.</param>
/// <param name="Passed">Кейсів, що пройшли.</param>
/// <param name="Total">Усього кейсів.</param>
/// <param name="Results">Результати в порядку кейсів.</param>
public sealed record EvaluationRun(
    Guid Id,
    Guid EvaluationId,
    Guid VersionId,
    int Version,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int Passed,
    int Total,
    IReadOnlyList<EvaluationCaseResult> Results);

/// <summary>Оцінка в списку.</summary>
/// <param name="Id">Id.</param>
/// <param name="WorkflowId">Сценарій.</param>
/// <param name="NodeId">Вузол.</param>
/// <param name="Name">Назва.</param>
/// <param name="CaseCount">Кейсів.</param>
/// <param name="LastRun">Останній прогін без результатів кейсів; <c>null</c> — не запускалась.</param>
public sealed record EvaluationSummary(Guid Id, Guid WorkflowId, string NodeId, string Name, int CaseCount, EvaluationRun? LastRun);

/// <summary>Оцінка з кейсами й останніми прогонами.</summary>
/// <param name="Summary">Заголовок.</param>
/// <param name="Cases">Кейси.</param>
/// <param name="Runs">До 10 останніх прогонів, найновіший першим.</param>
public sealed record EvaluationDetail(EvaluationSummary Summary, IReadOnlyList<EvaluationCase> Cases, IReadOnlyList<EvaluationRun> Runs);

/// <summary>Невалідна оцінка (422 <c>EVALUATION_INVALID</c>).</summary>
/// <param name="message">Що не так.</param>
public sealed class EvaluationInvalidException(string message) : OpenBpException("EVALUATION_INVALID", 422, message);

/// <summary>
/// Оцінки якості вузлів ШІ (F6-17, docs/architecture.md §10.3): набори кейсів «вхід → очікуване» для вузла сценарію,
/// прогін через той самий механізм, що <c>test_node</c>, і результати з прив'язкою до версії. REST
/// <c>/workflows/{id}/evaluations</c>, <c>/evaluations/{id}</c>, MCP <c>run_evaluation</c>.
/// Ролі в проєкті сценарію: читання — <see cref="UserRole.Viewer"/>, зміни й прогін — <see cref="UserRole.Editor"/>.
/// </summary>
public interface IEvaluationService
{
    /// <summary>Оцінки сценарію; <paramref name="nodeId"/> — лише цього вузла.</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="nodeId">Вузол або <c>null</c>.</param>
    /// <param name="ct">Скасування.</param>
    Task<IReadOnlyList<EvaluationSummary>> ListAsync(Guid workflowId, string? nodeId, CancellationToken ct);

    /// <summary>Оцінка з кейсами й прогонами.</summary>
    /// <param name="id">Оцінка.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Немає або сценарій іншого проєкту.</exception>
    Task<EvaluationDetail> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Створює оцінку для вузла поточної версії.</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="input">Вузол, назва, кейси.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="EvaluationInvalidException">Немає вузла, невідомий спосіб перевірки, бракує налаштувань.</exception>
    Task<EvaluationDetail> CreateAsync(Guid workflowId, EvaluationCreate input, CancellationToken ct);

    /// <summary>Змінює назву й замінює кейси.</summary>
    /// <param name="id">Оцінка.</param>
    /// <param name="input">Зміни.</param>
    /// <param name="ct">Скасування.</param>
    Task<EvaluationDetail> UpdateAsync(Guid id, EvaluationUpdate input, CancellationToken ct);

    /// <summary>Видаляє оцінку з прогонами.</summary>
    /// <param name="id">Оцінка.</param>
    /// <param name="ct">Скасування.</param>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Виконує вузол на кожному кейсі (по черзі, як <c>test_node</c> з items) на поточній версії, перевіряє результат і
    /// зберігає прогін.
    /// </summary>
    /// <param name="id">Оцінка.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="EvaluationInvalidException">Кейсів немає або вузла вже немає в поточній версії.</exception>
    Task<EvaluationRun> RunAsync(Guid id, CancellationToken ct);
}

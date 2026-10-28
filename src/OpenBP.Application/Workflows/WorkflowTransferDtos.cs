using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>Конектор, на який посилається експортований сценарій (<c>serverId</c> у параметрах вузлів).</summary>
/// <param name="Ref">Id сервера в середовищі-джерелі — так він записаний у визначенні.</param>
/// <param name="Name">Назва сервера; за нею (і транспортом) імпорт шукає відповідник.</param>
/// <param name="Transport">Транспорт: <c>http</c> або <c>stdio</c>; порожній — будь-який (імпорт з n8n).</param>
public sealed record ManifestConnector(string Ref, string Name, string Transport);

/// <summary>Credential, на який посилається експортований сценарій (<c>credentialId</c> у параметрах вузлів).</summary>
/// <param name="Ref">Id credential'а в середовищі-джерелі.</param>
/// <param name="Name">Назва; за нею (і типом) імпорт шукає відповідник.</param>
/// <param name="Type">Тип (<c>credentials.type</c>).</param>
public sealed record ManifestCredential(string Ref, string Name, string Type);

/// <summary>Інший сценарій, на який посилається експортований (<c>settings.errorWorkflowId</c>).</summary>
/// <param name="Ref">Id сценарію в середовищі-джерелі.</param>
/// <param name="Name">Назва; за нею імпорт шукає відповідник.</param>
public sealed record ManifestWorkflow(string Ref, string Name);

/// <summary>
/// Залежності сценарію, які треба знайти в середовищі-приймачі (F6-09). Секретів немає: лише id, назви й типи.
/// </summary>
/// <param name="Connectors">Конектори.</param>
/// <param name="Credentials">Credentials.</param>
/// <param name="Variables">Ключі змінних інстансу (<c>$vars.key</c>); значення не експортуються.</param>
/// <param name="Workflows">Сценарії помилок.</param>
public sealed record WorkflowManifest(
    IReadOnlyList<ManifestConnector> Connectors,
    IReadOnlyList<ManifestCredential> Credentials,
    IReadOnlyList<string> Variables,
    IReadOnlyList<ManifestWorkflow>? Workflows = null);

/// <summary>Результат <c>GET /workflows/{id}/export</c>: визначення поточної версії і маніфест залежностей.</summary>
/// <param name="Definition">Визначення як є, з id середовища-джерела.</param>
/// <param name="Manifest">Залежності.</param>
public sealed record WorkflowExport(WorkflowDefinition Definition, WorkflowManifest Manifest);

/// <summary>Явні відповідники «ref джерела → id приймача»; не вказане шукається за назвою і типом.</summary>
/// <param name="Connectors">Конектори.</param>
/// <param name="Credentials">Credentials.</param>
/// <param name="Workflows">Сценарії помилок.</param>
public sealed record ImportMapping(
    IReadOnlyDictionary<string, string>? Connectors = null,
    IReadOnlyDictionary<string, string>? Credentials = null,
    IReadOnlyDictionary<string, string>? Workflows = null);

/// <summary>Тіло <c>POST /workflows/import</c>.</summary>
/// <param name="Definition">Визначення з експорту.</param>
/// <param name="Manifest">Маніфест з експорту; без нього посилання мають уже бути id приймача.</param>
/// <param name="Mapping">Явні відповідники.</param>
/// <param name="GroupId">Група нового сценарію.</param>
public sealed record WorkflowImportRequest(
    WorkflowDefinition? Definition,
    WorkflowManifest? Manifest = null,
    ImportMapping? Mapping = null,
    Guid? GroupId = null)
{
    /// <summary>
    /// Сценарій, який замінює імпорт (повторний імпорт проєкту, F8-06): зберігається нова версія чернетки цього сценарію,
    /// а не створюється новий. Опублікована версія й активність не змінюються.
    /// </summary>
    public Guid? ReplaceWorkflowId { get; init; }
}

/// <summary>Посилання, для якого імпорт не знайшов відповідника (422 <c>IMPORT_UNMAPPED</c>).</summary>
/// <param name="Kind"><c>connector</c>, <c>credential</c> або <c>workflow</c>.</param>
/// <param name="Ref">Id у середовищі-джерелі.</param>
/// <param name="Name">Назва з маніфесту, якщо є.</param>
/// <param name="Type">Транспорт конектора або тип credential'а з маніфесту, якщо є.</param>
/// <param name="Reason"><c>not_found</c> — немає з такою назвою і типом; <c>ambiguous</c> — кілька; <c>invalid</c> — явний відповідник не існує.</param>
public sealed record UnmappedReference(string Kind, string Ref, string? Name, string? Type, string Reason);

/// <summary>Результат імпорту: створений (неактивний) сценарій і застосовані відповідники.</summary>
/// <param name="Workflow">Новий сценарій.</param>
/// <param name="Mapping">Усі відповідники, з автоматичними.</param>
/// <param name="MissingVariables">Ключі <c>$vars</c>, яких немає в приймачі (сценарій створено, але їх варто додати).</param>
public sealed record WorkflowImportResult(WorkflowDetail Workflow, ImportMapping Mapping, IReadOnlyList<string> MissingVariables);

/// <summary>Kind для <see cref="UnmappedReference"/>.</summary>
public static class ReferenceKinds
{
    /// <summary>MCP-сервер.</summary>
    public const string Connector = "connector";

    /// <summary>Credential.</summary>
    public const string Credential = "credential";

    /// <summary>Сценарій.</summary>
    public const string Workflow = "workflow";
}

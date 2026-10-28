using System.Text.Json.Nodes;
using OpenBP.Application.Assistant;
using OpenBP.Application.Auth;
using OpenBP.Data;

namespace OpenBP.Application.Audit;

/// <summary>Типи сутностей журналу аудиту (<c>audit_log.entity_type</c>).</summary>
public static class AuditEntities
{
    public const string Workflow = "workflow";
    public const string Credential = "credential";
    public const string Connector = "connector";
    public const string ApiKey = "apiKey";
    public const string Variable = "variable";
    public const string DataTable = "dataTable";
    public const string NotificationChannel = "notificationChannel";
    public const string Project = "project";
}

/// <summary>Дії журналу аудиту (<c>audit_log.action</c>): <c>&lt;сутність&gt;.&lt;дія&gt;</c>.</summary>
public static class AuditActions
{
    public const string WorkflowCreated = "workflow.created";
    public const string WorkflowSaved = "workflow.saved";
    public const string WorkflowRenamed = "workflow.renamed";
    public const string WorkflowPublished = "workflow.published";
    public const string WorkflowDraftDiscarded = "workflow.draftDiscarded";
    public const string WorkflowActivated = "workflow.activated";
    public const string WorkflowDeactivated = "workflow.deactivated";
    public const string WorkflowDeleted = "workflow.deleted";
    public const string CredentialCreated = "credential.created";
    public const string CredentialRenamed = "credential.renamed";
    public const string CredentialReplaced = "credential.replaced";
    public const string CredentialDeleted = "credential.deleted";
    public const string ConnectorCreated = "connector.created";
    public const string ConnectorUpdated = "connector.updated";
    public const string ConnectorDeleted = "connector.deleted";
    public const string ApiKeyCreated = "apiKey.created";
    public const string ApiKeyRevoked = "apiKey.revoked";
    public const string VariableSet = "variable.set";
    public const string VariableDeleted = "variable.deleted";
    public const string DataTableCreated = "dataTable.created";
    public const string DataTableUpdated = "dataTable.updated";
    public const string DataTableDeleted = "dataTable.deleted";
    /// <summary>Прочитано значення чутливих колонок (F8-02).</summary>
    public const string DataTableSensitiveRead = "dataTable.sensitiveRead";
    public const string NotificationChannelCreated = "notificationChannel.created";
    public const string NotificationChannelUpdated = "notificationChannel.updated";
    public const string NotificationChannelDeleted = "notificationChannel.deleted";
    public const string ProjectCreated = "project.created";
    public const string ProjectRenamed = "project.renamed";
    public const string ProjectDeleted = "project.deleted";
    public const string ProjectMemberSet = "project.memberSet";
    public const string ProjectMemberRemoved = "project.memberRemoved";
    public const string ProjectPageSaved = "project.pageSaved";
    public const string ProjectPageDeleted = "project.pageDeleted";
    /// <summary>Експорт проєкту шаблоном (F8-06).</summary>
    public const string ProjectExported = "project.exported";
    /// <summary>Імпорт шаблону проєкту (F8-06).</summary>
    public const string ProjectImported = "project.imported";
}

/// <summary>
/// Запис у журнал аудиту (F6-15). Додає рядок у той самий <see cref="OpenBpDbContext"/>, що й зміна, — він зберігається
/// тим самим <c>SaveChanges</c> (і в тій самій транзакції), тож дії без запису чи записів без дії не буває.
/// У <paramref name="details"/> — лише назви, типи й імена полів, ніколи значення секретів.
/// </summary>
public static class AuditTrail
{
    /// <summary>Додає запис; зберігає його наступний <c>SaveChangesAsync</c> контексту.</summary>
    /// <param name="db">Контекст зміни.</param>
    /// <param name="user">Хто змінює; <c>null</c> — система.</param>
    /// <param name="at">Коли.</param>
    /// <param name="action">Одна з <see cref="AuditActions"/>.</param>
    /// <param name="entityType">Одна з <see cref="AuditEntities"/>.</param>
    /// <param name="entityId">Id сутності (або ключ змінної).</param>
    /// <param name="details">Подробиці без секретів; зміна через загальний помічник отримує <c>via = "assistant"</c> (F3-113).</param>
    public static void Audit(
        this OpenBpDbContext db,
        ICurrentUser? user,
        DateTimeOffset at,
        string action,
        string entityType,
        string? entityId,
        JsonObject? details = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.AuditLog.Add(new AuditEntryEntity
        {
            Id = Guid.CreateVersion7(at),
            At = at,
            UserId = user?.IsAuthenticated == true ? user.UserId : null,
            ApiKeyId = user?.IsAuthenticated == true ? user.ApiKeyId : null,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = WithVia(details ?? []).ToJsonString(),
        });
    }

    private static JsonObject WithVia(JsonObject details)
    {
        if (AssistantCall.Active)
        {
            details["via"] = AssistantCall.Via;
        }

        return details;
    }
}

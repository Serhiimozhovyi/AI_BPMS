using System.Text.Json.Nodes;
using OpenBP.Application.Auth;
using OpenBP.Application.Paging;

namespace OpenBP.Application.Audit;

/// <summary>Запис журналу аудиту для REST і UI.</summary>
/// <param name="Id"><c>audit_log.id</c>.</param>
/// <param name="At">Коли.</param>
/// <param name="UserId">Хто; <c>null</c> — система.</param>
/// <param name="UserEmail">Email користувача, якщо він ще існує.</param>
/// <param name="ApiKeyId">Ключ, яким зроблено зміну; <c>null</c> — сесія UI.</param>
/// <param name="ApiKeyName">Назва ключа, якщо його не відкликано.</param>
/// <param name="Action">Дія (<see cref="AuditActions"/>).</param>
/// <param name="EntityType">Тип сутності (<see cref="AuditEntities"/>).</param>
/// <param name="EntityId">Id сутності.</param>
/// <param name="Details">Подробиці без секретів.</param>
public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset At,
    Guid? UserId,
    string? UserEmail,
    Guid? ApiKeyId,
    string? ApiKeyName,
    string Action,
    string EntityType,
    string? EntityId,
    JsonObject Details);

/// <summary>Фільтр <c>GET /audit</c>; усі поля необов'язкові й поєднуються через AND.</summary>
/// <param name="UserId">Хто змінював.</param>
/// <param name="Action">Точна дія (<c>workflow.saved</c>) або префікс із крапкою (<c>workflow.</c>).</param>
/// <param name="EntityType">Тип сутності.</param>
/// <param name="EntityId">Id сутності.</param>
/// <param name="From">Не раніше.</param>
/// <param name="To">Раніше ніж.</param>
public sealed record AuditFilter(
    Guid? UserId = null,
    string? Action = null,
    string? EntityType = null,
    string? EntityId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

/// <summary>Читання журналу аудиту (F6-15): лише <see cref="UserRole.Admin"/>, нові записи першими.</summary>
public interface IAuditService
{
    /// <summary>Сторінка записів за фільтром.</summary>
    Task<PageResult<AuditEntry>> ListAsync(AuditFilter filter, PageRequest page, CancellationToken ct);
}

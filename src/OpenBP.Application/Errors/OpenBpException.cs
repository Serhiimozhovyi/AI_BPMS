using OpenBP.Application.Auth;
using OpenBP.Shared.Validation;

namespace OpenBP.Application.Errors;

/// <summary>
/// Типізовані помилки application-шару (docs/architecture.md §10.1, §10.3). Api перетворює їх на
/// <c>application/problem+json</c> зі статусом <see cref="StatusCode"/> і полем <c>code</c> = <see cref="Code"/>;
/// MCP-сервер — на результат з <c>isError</c> і тим самим кодом.
/// </summary>
public abstract class OpenBpException : Exception
{
    /// <param name="code">Машинний код помилки (UPPER_SNAKE_CASE).</param>
    /// <param name="statusCode">HTTP-статус для REST.</param>
    /// <param name="message">Пояснення для людини.</param>
    /// <param name="inner">Причина.</param>
    protected OpenBpException(string code, int statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        StatusCode = statusCode;
    }

    /// <summary>Машинний код помилки.</summary>
    public string Code { get; }

    /// <summary>HTTP-статус для REST.</summary>
    public int StatusCode { get; }
}

/// <summary>401: запит не автентифіковано.</summary>
public sealed class UnauthenticatedException : OpenBpException
{
    public UnauthenticatedException()
        : base("UNAUTHENTICATED", 401, "Потрібна автентифікація.")
    {
    }
}

/// <summary>403: роль користувача нижча за потрібну.</summary>
public sealed class ForbiddenException : OpenBpException
{
    /// <param name="required">Мінімальна роль для дії.</param>
    public ForbiddenException(UserRole required)
        : base("FORBIDDEN", 403, $"Потрібна роль {required.ToString().ToLowerInvariant()} або вища.")
    {
        Required = required;
    }

    /// <summary>Мінімальна роль для дії.</summary>
    public UserRole Required { get; }
}

/// <summary>404: сутності з таким id немає (або вона не видима користувачу).</summary>
public sealed class NotFoundException : OpenBpException
{
    /// <param name="entity">Назва сутності, напр. <c>workflow</c>.</param>
    /// <param name="id">Ідентифікатор.</param>
    public NotFoundException(string entity, object id)
        : base("NOT_FOUND", 404, $"{entity} «{id}» не знайдено.")
    {
        Entity = entity;
        Id = id;
    }

    /// <summary>Назва сутності.</summary>
    public string Entity { get; }

    /// <summary>Ідентифікатор.</summary>
    public object Id { get; }
}

/// <summary>409: дія суперечить поточному стану (видалення активного сценарію, конектора з активними сценаріями, дублікат email).</summary>
public sealed class ConflictException : OpenBpException
{
    /// <param name="code">Уточнений код, напр. <c>WORKFLOW_ACTIVE</c>.</param>
    /// <param name="message">Пояснення.</param>
    public ConflictException(string code, string message)
        : base(code, 409, message)
    {
    }
}

/// <summary>422: визначення сценарію не пройшло валідацію; версія не створена.</summary>
public sealed class WorkflowValidationException : OpenBpException
{
    /// <param name="errors">Помилки валідації (<c>workflow-format.md</c> §«Валідація»).</param>
    public WorkflowValidationException(IReadOnlyList<WorkflowValidationError> errors)
        : base("WORKFLOW_INVALID", 422, $"Сценарій не пройшов валідацію: {errors?.Count ?? 0} помилок.")
    {
        Errors = errors ?? [];
    }

    /// <summary>Помилки валідації; у REST — поле <c>errors</c> problem-details.</summary>
    public IReadOnlyList<WorkflowValidationError> Errors { get; }
}

/// <summary>502: MCP-сервер конектора недоступний або не відповів за таймаут.</summary>
public sealed class ConnectorUnavailableException : OpenBpException
{
    /// <param name="connectorId">Id конектора (<c>mcp_servers.id</c>).</param>
    /// <param name="message">Пояснення.</param>
    /// <param name="inner">Причина (напр. <c>McpUnavailableException</c>).</param>
    public ConnectorUnavailableException(Guid connectorId, string message, Exception? inner = null)
        : base("CONNECTOR_UNAVAILABLE", 502, message, inner)
    {
        ConnectorId = connectorId;
    }

    /// <summary>Id конектора.</summary>
    public Guid ConnectorId { get; }
}

using OpenBP.Application.Auth;
using OpenBP.Application.Workflows;
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

/// <summary>422: запит коректний за формою, але не виконує правила дії (напр. відхилення без причини).</summary>
public sealed class UnprocessableException : OpenBpException
{
    /// <param name="code">Уточнений код, напр. <c>STAGE_RULE_REASON_REQUIRED</c>.</param>
    /// <param name="message">Пояснення.</param>
    public UnprocessableException(string code, string message)
        : base(code, 422, message)
    {
    }
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

/// <summary>400: дані credential'а не відповідають його типу (<c>GET /credentials/types</c>).</summary>
public sealed class CredentialValidationException : OpenBpException
{
    /// <param name="errors">Помилки за полями: <c>{ field, message }</c>.</param>
    public CredentialValidationException(IReadOnlyList<CredentialFieldError> errors)
        : base("CREDENTIAL_INVALID", 400, $"Дані облікових даних некоректні: {errors?.Count ?? 0} помилок.")
    {
        Errors = errors ?? [];
    }

    /// <summary>Помилки; у REST — поле <c>errors</c> problem-details.</summary>
    public IReadOnlyList<CredentialFieldError> Errors { get; }
}

/// <summary>Помилка поля credential'а.</summary>
/// <param name="Field">Назва поля (<c>apiKey</c>, <c>headers</c>, <c>type</c>…).</param>
/// <param name="Message">Пояснення українською.</param>
public sealed record CredentialFieldError(string Field, string Message);

/// <summary>Імпорт сценарію: для частини посилань немає відповідника в цьому середовищі (F6-09). Відповідь містить <c>unmapped[]</c>.</summary>
public sealed class ImportUnmappedException : OpenBpException
{
    /// <summary>Створює помилку.</summary>
    /// <param name="unmapped">Посилання без відповідника.</param>
    public ImportUnmappedException(IReadOnlyList<UnmappedReference> unmapped)
        : base("IMPORT_UNMAPPED", 422, $"Для {unmapped?.Count ?? 0} посилань сценарію немає відповідника — вкажіть mapping.")
    {
        Unmapped = unmapped ?? [];
    }

    /// <summary>Посилання без відповідника.</summary>
    public IReadOnlyList<UnmappedReference> Unmapped { get; }
}

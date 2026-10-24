namespace OpenBP.Data;

// Сутності відповідають docs/architecture.md §6. jsonb-колонки зберігаються як сирий JSON-рядок.

public sealed class UserEntity
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    /// <summary><c>null</c> — користувач входить лише через OIDC (F4-14).</summary>
    public string? PasswordHash { get; set; }
    public string Role { get; set; } = "viewer";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Зв'язок користувача з обліковим записом OIDC-провайдера (F4-14): <c>(provider, subject)</c> унікальні.</summary>
public sealed class UserLoginEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Ключ провайдера з <c>OpenBp:Oidc:Providers:&lt;key&gt;</c>.</summary>
    public string Provider { get; set; } = "";
    /// <summary>Claim <c>sub</c> провайдера.</summary>
    public string Subject { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class ApiKeyEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class WorkflowEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Група сценарію (<c>workflow_groups.id</c>); <c>null</c> — без групи.</summary>
    public Guid? GroupId { get; set; }
}

/// <summary>Група сценаріїв (F3-63): плоский список поза визначенням сценарію.</summary>
public sealed class WorkflowGroupEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Назва у верхньому регістрі (інваріантна культура) — унікальність без урахування регістру.</summary>
    public string NormalizedName { get; set; } = "";
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkflowVersionEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public int Version { get; set; }
    /// <summary>jsonb: формат workflow.</summary>
    public string Definition { get; set; } = "{}";
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CredentialEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public byte[] DataEncrypted { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public Guid? CreatedBy { get; set; }
}

public sealed class McpServerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Transport { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string Config { get; set; } = "{}";
    public Guid? CredentialId { get; set; }
    public string Status { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string? ToolsCache { get; set; }
    public DateTimeOffset? ToolsCachedAt { get; set; }
}

public sealed class ExecutionEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid VersionId { get; set; }
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public string? Error { get; set; }
}

public sealed class ExecutionStepEntity
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public string NodeId { get; set; } = "";
    public int RunIndex { get; set; }
    public string Status { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string? Input { get; set; }
    /// <summary>jsonb.</summary>
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class JobEntity
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>jsonb.</summary>
    public string Payload { get; set; } = "{}";
    public DateTimeOffset RunAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public int Attempts { get; set; }
}

public sealed class WebhookEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public string NodeId { get; set; } = "";
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = "";
}

public sealed class NodeStateEntity
{
    public Guid WorkflowId { get; set; }
    public string NodeId { get; set; } = "";
    /// <summary>jsonb: стан polling-тригера.</summary>
    public string State { get; set; } = "{}";
}

/// <summary>Незавершений вхід OAuth2 authorization code (F4-04): одноразовий <c>state</c> з PKCE-верифікатором.</summary>
public sealed class OAuthStateEntity
{
    public string State { get; set; } = "";
    public Guid CredentialId { get; set; }
    public Guid UserId { get; set; }
    public string CodeVerifier { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

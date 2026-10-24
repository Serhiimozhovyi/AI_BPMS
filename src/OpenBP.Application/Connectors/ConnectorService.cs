using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Audit;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Notifications;
using OpenBP.Application.Paging;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Mcp.Client;

namespace OpenBP.Application.Connectors;

/// <summary>
/// Реалізація <see cref="IConnectorService"/> над <c>mcp_servers</c> (docs/architecture.md §10.1, §10.7).
/// Після кожної зміни синхронізує <see cref="IMcpServerRegistry"/> процесу Api: реєструє сервер із
/// налаштуваннями, злитими з credential'ом (<c>{ "headers" }</c>/<c>{ "env" }</c>), і переносить кеш tools,
/// щоб валідатор <c>mcp.*</c> бачив схеми. Значення credential'ів не потрапляють ні в <c>config</c>, ні у відповіді.
/// </summary>
public sealed class ConnectorService(
    OpenBpDbContext db,
    ICurrentUser currentUser,
    IMcpServerRegistry registry,
    Func<ICredentialProvider> credentials,
    TimeProvider time) : IConnectorService
{
    /// <summary>Код конфлікту: на конектор посилаються активні сценарії.</summary>
    public const string ConnectorInUseCode = "CONNECTOR_IN_USE";

    private const string Entity = "connector";

    private readonly ProjectAccess _access = new(db, currentUser);

    /// <inheritdoc />
    public async Task<PageResult<ConnectorSummary>> ListAsync(PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        currentUser.Require(UserRole.Viewer);
        page = page.Normalized();

        var query = db.McpServers.AsNoTracking();
        if (await _access.VisibleProjectsAsync(ct) is { } visible)
        {
            query = query.Where(s => s.ProjectId == null || visible.Contains(s.ProjectId.Value));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(s => s.Name).ThenBy(s => s.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
        return new PageResult<ConnectorSummary>([.. rows.Select(r => Summary(r, ConnectorRegistration.ReadTools(r)))], page.Page, page.PageSize, total);
    }

    /// <inheritdoc />
    public async Task<ConnectorDetail> GetAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var row = await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct)
                  ?? throw new NotFoundException(Entity, id);
        await RequireVisibleAsync(row, ct);
        return Detail(row);
    }

    /// <inheritdoc />
    public async Task<ConnectorDetail> CreateAsync(ConnectorInput input, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        ArgumentNullException.ThrowIfNull(input);
        await RequireProjectAsync(input.ProjectId, ct);
        await _access.RequireManageAsync(input.ProjectId, "workflow-group", input.ProjectId?.ToString() ?? "none", ct);
        var (name, config) = await ValidateAsync(input, ct);
        var row = new McpServerEntity
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            Name = name,
            Transport = ConnectorRegistration.TransportValue(input.Transport),
            Config = JsonSerializer.Serialize(config, ConnectorRegistration.Json),
            CredentialId = input.CredentialId,
            Status = ConnectorRegistration.StatusValue(ConnectorStatus.New),
            ProjectId = input.ProjectId,
        };
        db.McpServers.Add(row);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ConnectorCreated, AuditEntities.Connector, row.Id.ToString(), AuditDetails(row));
        await db.SaveChangesAsync(ct);

        // Недоступний сервер — не помилка створення: конектор лишається зі станом error.
        await DiscoverAsync(row, ct);
        return Detail(row);
    }

    /// <inheritdoc />
    public async Task<ConnectorDetail> UpdateAsync(Guid id, ConnectorInput input, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        ArgumentNullException.ThrowIfNull(input);
        var row = await FindManagedAsync(id, ct);
        if (input.ProjectId != row.ProjectId)
        {
            await RequireProjectAsync(input.ProjectId, ct);
            await _access.RequireManageAsync(input.ProjectId, "workflow-group", input.ProjectId?.ToString() ?? "none", ct);
        }

        var (name, config) = await ValidateAsync(input, ct);
        row.ProjectId = input.ProjectId;
        row.Name = name;
        row.Transport = ConnectorRegistration.TransportValue(input.Transport);
        row.Config = JsonSerializer.Serialize(config, ConnectorRegistration.Json);
        row.CredentialId = input.CredentialId;
        row.Status = ConnectorRegistration.StatusValue(ConnectorStatus.New);
        row.ToolsCache = null;
        row.ToolsCachedAt = null;
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ConnectorUpdated, AuditEntities.Connector, id.ToString(), AuditDetails(row));
        await db.SaveChangesAsync(ct);

        await DiscoverAsync(row, ct);
        return Detail(row);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var row = await FindManagedAsync(id, ct);

        var definitions = await db.Workflows.AsNoTracking().Where(w => w.Active)
            .Join(db.WorkflowVersions, w => w.CurrentVersionId, v => v.Id, (w, v) => new { w.Name, v.Definition })
            .ToListAsync(ct);
        var serverId = id.ToString();
        var users = definitions.Where(d => References(d.Definition, serverId)).Select(d => d.Name).ToList();
        if (users.Count > 0)
        {
            throw new ConflictException(
                ConnectorInUseCode,
                $"Конектор «{row.Name}» використовують активні сценарії: {string.Join(", ", users.Select(u => $"«{u}»"))}.");
        }

        db.McpServers.Remove(row);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ConnectorDeleted, AuditEntities.Connector, serverId, AuditDetails(row));
        await db.SaveChangesAsync(ct);
        await registry.UnregisterAsync(serverId);
    }

    /// <summary>Подробиці для журналу аудиту: без конфігурації (у ній можуть бути заголовки й змінні оточення з секретами).</summary>
    private static JsonObject AuditDetails(McpServerEntity row) => new()
    {
        ["name"] = row.Name,
        ["transport"] = row.Transport,
        ["credentialId"] = row.CredentialId?.ToString(),
    };

    /// <inheritdoc />
    public async Task<ConnectorDetail> RefreshToolsAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var row = await FindManagedAsync(id, ct);
        var error = await DiscoverAsync(row, ct);
        if (error is not null)
        {
            throw new ConnectorUnavailableException(id, error.Message, error);
        }

        return Detail(row);
    }

    /// <inheritdoc />
    public async Task<ToolCallResult> CallToolAsync(Guid id, string toolName, JsonObject arguments, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var row = await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        await RequireVisibleAsync(row, ct);
        var scope = await _access.ScopeAsync(ct);
        var canCall = row.ProjectId is null
            ? scope.All || scope.SharedRole >= UserRole.Editor || scope.Projects.Values.Any(r => r >= UserRole.Editor)
            : scope.RoleIn(row.ProjectId) >= UserRole.Editor;
        if (!canCall)
        {
            throw new ForbiddenException(UserRole.Editor);
        }

        if (!ConnectorRegistration.ReadTools(row).Any(t => string.Equals(t.Name, toolName, StringComparison.Ordinal)))
        {
            throw new NotFoundException("tool", toolName);
        }

        var serverId = id.ToString();
        if (!registry.TryGetServer(serverId, out _))
        {
            var registrationError = await RegisterAsync(row, ct);
            if (registrationError is not null)
            {
                await SetStatusAsync(row, ConnectorStatus.Error, ct);
                throw new ConnectorUnavailableException(id, registrationError.Message, registrationError);
            }
        }

        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in arguments ?? [])
        {
            args[key] = value is null ? JsonDocument.Parse("null").RootElement.Clone() : JsonSerializer.SerializeToElement(value);
        }

        var started = time.GetTimestamp();
        CallToolResult result;
        try
        {
            result = await registry.RunAsync(
                serverId,
                (client, token) => client.CallToolAsync(new CallToolRequestParams { Name = toolName, Arguments = args }, token).AsTask(),
                ct: ct);
        }
        catch (McpUnavailableException ex)
        {
            await SetStatusAsync(row, ConnectorStatus.Error, ct);
            throw new ConnectorUnavailableException(id, ex.Message, ex);
        }
        catch (McpProtocolException ex)
        {
            throw new ArgumentException($"Tool «{toolName}» відхилив виклик: {ex.Message}", nameof(arguments), ex);
        }

        var elapsed = time.GetElapsedTime(started);
        await SetStatusAsync(row, ConnectorStatus.Ok, ct);

        JsonObject? structured = null;
        if (result.StructuredContent is { } content)
        {
            var node = JsonSerializer.SerializeToNode(content);
            structured = node as JsonObject ?? new JsonObject { ["result"] = node };
        }

        var blocks = result.Content
            .Select(b => JsonSerializer.SerializeToNode(b, McpJsonUtilities.DefaultOptions) as JsonObject ?? [])
            .ToList();
        return new ToolCallResult(result.IsError == true, structured, blocks, elapsed);
    }

    /// <summary>
    /// Реєструє в реєстрі процесу всі конектори з <c>mcp_servers</c> разом із збереженим кешем tools — при старті Api,
    /// щоб валідатор <c>mcp.*</c> і тестові виклики працювали без повторного discovery. Без перевірки ролей;
    /// конектор, який не можна підключити (credential), пропускається.
    /// </summary>
    /// <param name="ct">Скасування.</param>
    /// <returns>Кількість зареєстрованих конекторів.</returns>
    public async Task<int> LoadRegistryAsync(CancellationToken ct)
    {
        var rows = await db.McpServers.AsNoTracking().ToListAsync(ct);
        var registered = 0;
        foreach (var row in rows)
        {
            try
            {
                if (await RegisterAsync(row, ct) is null)
                {
                    registered++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Напр., немає OPENBP_ENCRYPTION_KEY: конектор зареєструється при першому discovery чи виклику.
            }
        }

        return registered;
    }

    private async Task<(string Name, ConnectorConfig Config)> ValidateAsync(ConnectorInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("Назва конектора обов'язкова.", nameof(input));
        }

        if (!Enum.IsDefined(input.Transport))
        {
            throw new ArgumentException($"Невідомий транспорт «{input.Transport}».", nameof(input));
        }

        var config = ConnectorRegistration.Normalize(input.Transport, input.Config);
        if (input.CredentialId is { } credentialId
            && !await db.Credentials.AnyAsync(c => c.Id == credentialId && (c.ProjectId == null || c.ProjectId == input.ProjectId), ct))
        {
            // Credential іншого проєкту для цього конектора — те саме, що його немає (F6-14).
            throw new NotFoundException("credential", credentialId);
        }

        return (input.Name.Trim(), config);
    }

    /// <summary>
    /// Реєструє сервер у реєстрі (сесія перевідкривається) і виконує <c>tools/list</c>; результат — у рядок.
    /// </summary>
    /// <returns>Помилка discovery або <c>null</c>, якщо успішно.</returns>
    private async Task<Exception?> DiscoverAsync(McpServerEntity row, CancellationToken ct)
    {
        var error = await RegisterAsync(row, ct);
        if (error is null)
        {
            try
            {
                var cache = await registry.RefreshToolsAsync(row.Id.ToString(), ct);
                row.ToolsCache = JsonSerializer.Serialize(cache.Tools, ConnectorRegistration.Json);
                row.ToolsCachedAt = cache.CachedAt;
                row.Status = ConnectorRegistration.StatusValue(ConnectorStatus.Ok);
                await db.SaveChangesAsync(ct);
                return null;
            }
            catch (McpUnavailableException ex)
            {
                error = ex;
            }
            catch (McpProtocolException ex)
            {
                error = new InvalidOperationException($"MCP-сервер «{row.Name}» відхилив tools/list: {ex.Message}", ex);
            }
        }

        await SetStatusAsync(row, ConnectorStatus.Error, ct);
        return error;
    }

    /// <summary>Реєструє сервер (з кешем tools рядка); помилку credential'а чи налаштувань повертає, не кидає.</summary>
    private async Task<Exception?> RegisterAsync(McpServerEntity row, CancellationToken ct)
    {
        var serverId = row.Id.ToString();
        McpServerConfig config;
        try
        {
            JsonObject? credential = null;
            if (row.CredentialId is { } credentialId)
            {
                credential = await credentials().GetAsync(credentialId.ToString(), ct);
            }

            config = ConnectorRegistration.Build(row, credential);
        }
        catch (Exception ex) when (ex is CredentialNotFoundException or CredentialDecryptionException or InvalidOperationException)
        {
            // Повідомлення без даних credential'а; старі налаштування з реєстру прибираються.
            await registry.UnregisterAsync(serverId);
            return new InvalidOperationException($"Конектор «{row.Name}» не можна підключити: {ex.Message}", ex);
        }

        await registry.RegisterAsync(config);
        var tools = ConnectorRegistration.ReadTools(row);
        if (tools.Count > 0)
        {
            registry.SetToolsCache(serverId, new ToolsCache(tools, row.ToolsCachedAt ?? time.GetUtcNow()));
        }

        return null;
    }

    /// <summary>
    /// Зберігає статус; перехід у <see cref="ConnectorStatus.Error"/> дає подію сповіщення
    /// <see cref="NotificationEvents.ConnectorUnavailable"/> (F6-07) — одну на перехід, а не на кожен невдалий виклик.
    /// </summary>
    private async Task SetStatusAsync(McpServerEntity row, ConnectorStatus status, CancellationToken ct)
    {
        var value = ConnectorRegistration.StatusValue(status);
        if (row.Status != value)
        {
            row.Status = value;
            if (status == ConnectorStatus.Error)
            {
                db.Notify(time.GetUtcNow(), NotificationEvents.ConnectorUnavailable, row.Id.ToString(), $"Конектор «{row.Name}» недоступний",
                    new JsonObject { ["connector"] = row.Name, ["connectorId"] = row.Id.ToString(), ["transport"] = row.Transport });
            }

            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Чи посилається визначення на сервер у <c>parameters.serverId</c> вузла <c>mcp.*</c> або в інструментах
    /// <c>ai.agent</c> (<c>parameters.tools[].serverId</c>).
    /// </summary>
    private static bool References(string definition, string serverId)
    {
        JsonNode? document;
        try
        {
            document = JsonNode.Parse(definition);
        }
        catch (JsonException)
        {
            return false;
        }

        if (document?["nodes"] is not JsonArray nodes)
        {
            return false;
        }

        foreach (var node in nodes.OfType<JsonObject>())
        {
            var type = node["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "";
            if (type.StartsWith("mcp.", StringComparison.Ordinal) && Same(node["parameters"]?["serverId"]))
            {
                return true;
            }

            if (type == "ai.agent" && node["parameters"]?["tools"] is JsonArray tools && tools.Any(tool => Same(tool?["serverId"])))
            {
                return true;
            }
        }

        return false;

        bool Same(JsonNode? value) =>
            value is JsonValue v && v.TryGetValue<string>(out var s) && string.Equals(s, serverId, StringComparison.OrdinalIgnoreCase);
    }

    private static ConnectorSummary Summary(McpServerEntity row, IReadOnlyList<McpToolInfo> tools) => new(
        row.Id,
        row.Name,
        ConnectorRegistration.ParseTransport(row.Transport),
        ConnectorRegistration.ParseStatus(row.Status),
        tools.Count,
        row.ToolsCachedAt,
        row.ProjectId);

    /// <summary>Конектор чужого проєкту виглядає як відсутній (F6-14).</summary>
    private async Task RequireVisibleAsync(McpServerEntity row, CancellationToken ct)
    {
        if (row.ProjectId is not null && await _access.RoleInAsync(row.ProjectId, ct) is null)
        {
            throw new NotFoundException(Entity, row.Id);
        }
    }

    /// <summary>Конектор для зміни: спільний — лише admin, проєктний — editor проєкту (F6-14).</summary>
    private async Task<McpServerEntity> FindManagedAsync(Guid id, CancellationToken ct)
    {
        var row = await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        await _access.RequireManageAsync(row.ProjectId, Entity, id, ct);
        return row;
    }

    private async Task RequireProjectAsync(Guid? projectId, CancellationToken ct)
    {
        if (projectId is { } id && !await db.WorkflowGroups.AnyAsync(g => g.Id == id, ct))
        {
            throw new NotFoundException("workflow-group", id);
        }
    }

    private static ConnectorDetail Detail(McpServerEntity row)
    {
        var tools = ConnectorRegistration.ReadTools(row);
        var config = ConnectorRegistration.ReadConfig(row);
        return new ConnectorDetail(Summary(row, tools), config, row.CredentialId, tools);
    }
}

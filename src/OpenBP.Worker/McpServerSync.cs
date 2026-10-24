using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Connectors;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker;

/// <summary>Конектор, на який посилається сценарій, не можна підключити (немає credential, некоректний <c>config</c>).</summary>
/// <param name="message">Повідомлення без секретів.</param>
public sealed class ConnectorConfigurationException(string message) : Exception(message);

/// <summary>
/// Синхронізує <see cref="IMcpServerRegistry"/> воркера з <c>mcp_servers</c> перед виконанням (docs/architecture.md §10.7):
/// для серверів, на які посилаються вузли <c>mcp.*</c> сценарію (<c>parameters.serverId</c> = <c>mcp_servers.id</c>),
/// реєструє відсутні й перереєстровує змінені за <c>transport</c>/<c>config</c>/<c>credential_id</c>/даними credential'а.
/// <list type="bullet">
/// <item>Credential конектора — <c>{ "headers": { … } }</c> (http) або <c>{ "env": { … } }</c> (stdio) — зливається в
/// <see cref="McpServerConfig"/>; його значення реєструються в <see cref="SecretScrubber"/> виконання.</item>
/// <item><c>config</c> — JSON <see cref="ConnectorConfig"/> у camelCase; <c>tools_cache</c> (масив <see cref="McpToolInfo"/>),
/// якщо є, стає кешем tools сервера — за ним <c>mcp.tool</c> приводить типи аргументів.</item>
/// <item>Сервер, якого немає в <c>mcp_servers</c>, знімається з реєстру: вузол завершиться помилкою «сервер невідомий».</item>
/// </list>
/// Один екземпляр на процес; потокобезпечний.
/// </summary>
/// <param name="contexts">Фабрика контекстів БД.</param>
/// <param name="registry">Реєстр MCP-серверів воркера.</param>
/// <param name="credentials">Сховище credentials.</param>
/// <param name="time">Годинник для часу кешу tools без <c>tools_cached_at</c>.</param>
public sealed class McpServerSync(
    IDbContextFactory<OpenBpDbContext> contexts,
    IMcpServerRegistry registry,
    CredentialStore credentials,
    TimeProvider? time = null)
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Id серверів, на які посилаються вузли <c>mcp.*</c> (<c>serverId</c>) та інструменти <c>ai.agent</c>
    /// (<c>tools[].serverId</c>, F6-12) сценарію — без виразів.
    /// </summary>
    /// <param name="workflow">Сценарій.</param>
    public static IReadOnlySet<string> ReferencedServers(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in workflow.Nodes)
        {
            if (node.Type.StartsWith("mcp.", StringComparison.Ordinal))
            {
                Add(node.Parameters["serverId"]);
            }
            else if (node.Type == "ai.agent" && node.Parameters["tools"] is JsonArray tools)
            {
                foreach (var tool in tools)
                {
                    Add(tool?["serverId"]);
                }
            }
        }

        return result;

        void Add(JsonNode? value)
        {
            if (value is JsonValue v
                && v.TryGetValue<string>(out var serverId)
                && !string.IsNullOrWhiteSpace(serverId)
                && !ExpressionSyntax.ContainsExpression(serverId))
            {
                result.Add(serverId);
            }
        }
    }

    /// <summary>Готує реєстр до виконання сценарію.</summary>
    /// <param name="workflow">Сценарій.</param>
    /// <param name="scrubber">Scrubber виконання: отримує значення credentials конекторів.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConnectorConfigurationException">Конектор не можна підключити.</exception>
    public async Task SyncAsync(WorkflowDefinition workflow, SecretScrubber scrubber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scrubber);
        var referenced = ReferencedServers(workflow);
        if (referenced.Count == 0)
        {
            return;
        }

        var ids = referenced.Select(s => Guid.TryParse(s, out var id) ? id : (Guid?)null).OfType<Guid>().ToList();
        List<McpServerEntity> rows;
        await using (var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            rows = await db.McpServers.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct).ConfigureAwait(false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var serverId in referenced)
            {
                var row = Guid.TryParse(serverId, out var id) ? rows.Find(r => r.Id == id) : null;
                if (row is null)
                {
                    if (_fingerprints.Remove(serverId))
                    {
                        await registry.UnregisterAsync(serverId).ConfigureAwait(false);
                    }

                    continue;
                }

                var credential = await LoadCredentialAsync(row, ct).ConfigureAwait(false);
                scrubber.Add(credential);
                var fingerprint = Fingerprint(row, credential);
                if (_fingerprints.TryGetValue(serverId, out var known) && known == fingerprint && registry.TryGetServer(serverId, out _))
                {
                    continue;
                }

                await registry.RegisterAsync(Build(serverId, row, credential)).ConfigureAwait(false);
                _fingerprints[serverId] = fingerprint;
                if (ReadToolsCache(row) is { } cache)
                {
                    registry.SetToolsCache(serverId, cache);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonObject?> LoadCredentialAsync(McpServerEntity row, CancellationToken ct)
    {
        if (row.CredentialId is not { } credentialId)
        {
            return null;
        }

        try
        {
            return await credentials.GetDataAsync(credentialId, ct).ConfigureAwait(false)
                ?? throw new ConnectorConfigurationException($"Конектор «{row.Name}»: credential {credentialId} не знайдено.");
        }
        catch (CredentialDecryptionException)
        {
            throw new ConnectorConfigurationException($"Конектор «{row.Name}»: credential {credentialId} не розшифровується.");
        }
    }

    private static McpServerConfig Build(string serverId, McpServerEntity row, JsonObject? credential)
    {
        ConnectorConfig config;
        try
        {
            config = JsonSerializer.Deserialize<ConnectorConfig>(row.Config, ConfigJson) ?? new ConnectorConfig();
        }
        catch (JsonException)
        {
            throw new ConnectorConfigurationException($"Конектор «{row.Name}»: некоректний config.");
        }

        switch (row.Transport)
        {
            case "stdio" when !string.IsNullOrWhiteSpace(config.Command):
                var env = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var (key, value) in config.Environment ?? new Dictionary<string, string>())
                {
                    env[key] = value;
                }

                foreach (var (key, value) in Section(row, credential, "env"))
                {
                    env[key] = value;
                }

                return new McpServerConfig
                {
                    Id = serverId,
                    Name = row.Name,
                    Transport = McpTransportKind.Stdio,
                    Command = config.Command,
                    Arguments = config.Arguments ?? [],
                    Environment = env.Count == 0 ? null : env,
                    WorkingDirectory = config.WorkingDirectory,
                };
            case "http" when config.Url is { IsAbsoluteUri: true } url:
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in config.Headers ?? new Dictionary<string, string>())
                {
                    headers[key] = value;
                }

                foreach (var (key, value) in Section(row, credential, "headers"))
                {
                    headers[key] = value;
                }

                // oauth2-credential (F4-04): токен — у заголовок Authorization.
                if (OAuth2Credential.AuthorizationHeader(credential) is { } authorization)
                {
                    headers["Authorization"] = authorization;
                }

                return new McpServerConfig
                {
                    Id = serverId,
                    Name = row.Name,
                    Transport = McpTransportKind.Http,
                    Url = url,
                    Headers = headers.Count == 0 ? null : headers,
                };
            case "stdio":
                throw new ConnectorConfigurationException($"Конектор «{row.Name}»: stdio без command.");
            case "http":
                throw new ConnectorConfigurationException($"Конектор «{row.Name}»: http без коректного url.");
            default:
                throw new ConnectorConfigurationException($"Конектор «{row.Name}»: невідомий транспорт «{row.Transport}».");
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> Section(McpServerEntity row, JsonObject? credential, string name)
    {
        if (credential?[name] is null)
        {
            yield break;
        }

        if (credential[name] is not JsonObject section)
        {
            throw new ConnectorConfigurationException($"Конектор «{row.Name}»: у credential «{name}» має бути об'єктом.");
        }

        foreach (var (key, value) in section)
        {
            if (value is not JsonValue v || !v.TryGetValue<string>(out var text))
            {
                throw new ConnectorConfigurationException($"Конектор «{row.Name}»: значення «{name}.{key}» у credential має бути рядком.");
            }

            yield return new(key, text);
        }
    }

    private ToolsCache? ReadToolsCache(McpServerEntity row)
    {
        if (string.IsNullOrWhiteSpace(row.ToolsCache))
        {
            return null;
        }

        try
        {
            var tools = JsonSerializer.Deserialize<List<McpToolInfo>>(row.ToolsCache, ConfigJson);
            return tools is null ? null : new ToolsCache(tools, row.ToolsCachedAt ?? _time.GetUtcNow());
        }
        catch (JsonException)
        {
            // Кеш лише допомагає привести типи аргументів; без нього виклики працюють.
            return null;
        }
    }

    private static string Fingerprint(McpServerEntity row, JsonObject? credential)
    {
        // Хеш, а не самі значення: дані credential'а не тримаються в пам'яті довше за виконання.
        var text = string.Join('\n', row.Transport, row.Config, row.CredentialId, credential?.ToJsonString(), row.Name);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}

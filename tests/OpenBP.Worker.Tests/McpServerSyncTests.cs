using System.Text.Json.Nodes;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Mcp.Client;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>Синхронізація реєстру MCP-серверів воркера з <c>mcp_servers</c> (docs/architecture.md §10.7).</summary>
[Collection(PostgresCollection.Name)]
public sealed class McpServerSyncTests(PostgresFixture postgres)
{
    [Fact]
    public async Task HttpConnector_CredentialHeadersMerged_AndRegisteredAsSecrets()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var credentialId = await worker.Credentials.CreateAsync(
            "token", "mcp", new JsonObject { ["headers"] = new JsonObject { ["Authorization"] = "Bearer super-secret-token" } }, null, default);
        var server = await AddServerAsync(worker, "http", new JsonObject { ["url"] = "http://127.0.0.1:1/mcp", ["headers"] = new JsonObject { ["X-Tenant"] = "a" } }, credentialId);
        var scrubber = new SecretScrubber();

        await worker.Servers.SyncAsync(Workflow(server.Id.ToString()), scrubber, default);

        Assert.True(worker.Mcp.TryGetServer(server.Id.ToString(), out var config));
        Assert.Equal(McpTransportKind.Http, config.Transport);
        Assert.Equal(new Uri("http://127.0.0.1:1/mcp"), config.Url);
        Assert.Equal("a", config.Headers!["X-Tenant"]);
        Assert.Equal("Bearer super-secret-token", config.Headers["Authorization"]);
        Assert.Equal("помилка: ***", scrubber.Scrub("помилка: Bearer super-secret-token"));
    }

    [Fact]
    public async Task StdioConnector_CredentialEnvMerged_ToolsCacheLoaded()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var credentialId = await worker.Credentials.CreateAsync(
            "env", "mcp", new JsonObject { ["env"] = new JsonObject { ["API_KEY"] = "k-123456" } }, null, default);
        var server = await AddServerAsync(
            worker,
            "stdio",
            new JsonObject { ["command"] = "dotnet", ["arguments"] = new JsonArray("srv.dll"), ["environment"] = new JsonObject { ["MODE"] = "test" } },
            credentialId,
            toolsCache: """[ { "name": "echo", "inputSchema": { "type": "object" } } ]""");

        await worker.Servers.SyncAsync(Workflow(server.Id.ToString()), new SecretScrubber(), default);

        Assert.True(worker.Mcp.TryGetServer(server.Id.ToString(), out var config));
        Assert.Equal("dotnet", config.Command);
        Assert.Equal(["srv.dll"], config.Arguments);
        Assert.Equal("test", config.Environment!["MODE"]);
        Assert.Equal("k-123456", config.Environment["API_KEY"]);
        Assert.NotNull(worker.Mcp.GetToolsCache(server.Id.ToString())?.Find("echo"));
    }

    [Fact]
    public async Task ChangedConfig_ReRegisters_DeletedServer_Unregisters()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var server = await AddServerAsync(worker, "http", new JsonObject { ["url"] = "http://127.0.0.1:1/mcp" });
        var workflow = Workflow(server.Id.ToString());
        await worker.Servers.SyncAsync(workflow, new SecretScrubber(), default);

        await using (var db = worker.Db.CreateDbContext())
        {
            var row = await db.McpServers.FindAsync(server.Id);
            row!.Config = new JsonObject { ["url"] = "http://127.0.0.1:2/mcp" }.ToJsonString();
            await db.SaveChangesAsync();
        }

        await worker.Servers.SyncAsync(workflow, new SecretScrubber(), default);
        Assert.True(worker.Mcp.TryGetServer(server.Id.ToString(), out var config));
        Assert.Equal(2, config.Url!.Port);

        await using (var db = worker.Db.CreateDbContext())
        {
            db.McpServers.Remove((await db.McpServers.FindAsync(server.Id))!);
            await db.SaveChangesAsync();
        }

        await worker.Servers.SyncAsync(workflow, new SecretScrubber(), default);
        Assert.False(worker.Mcp.TryGetServer(server.Id.ToString(), out _));
    }

    [Fact]
    public async Task StdioConnector_GenericCredential_FieldsMappedToEnv_ComputedValuesScrubbed()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var credentialId = await worker.Credentials.CreateAsync(
            "db", "database",
            new JsonObject { ["engine"] = "postgres", ["host"] = "db.local", ["database"] = "crm", ["username"] = "reader", ["password"] = "pw-secret-1", ["ssl"] = "require" },
            null, default);
        var server = await AddServerAsync(
            worker,
            "stdio",
            new JsonObject
            {
                ["command"] = "dotnet",
                ["environment"] = new JsonObject { ["SQL_MAX_ROWS"] = "10" },
                ["credentialMap"] = new JsonObject { ["engine"] = "SQL_PROVIDER", ["$connectionString"] = "SQL_CONNECTION_STRING" },
            },
            credentialId);
        var scrubber = new SecretScrubber();

        await worker.Servers.SyncAsync(Workflow(server.Id.ToString()), scrubber, default);

        Assert.True(worker.Mcp.TryGetServer(server.Id.ToString(), out var config));
        Assert.Equal("10", config.Environment!["SQL_MAX_ROWS"]);
        Assert.Equal("postgres", config.Environment["SQL_PROVIDER"]);
        var connectionString = config.Environment["SQL_CONNECTION_STRING"]!;
        Assert.Contains("Password=pw-secret-1", connectionString, StringComparison.Ordinal);
        Assert.Equal("помилка: ***", scrubber.Scrub($"помилка: {connectionString}"));
        // Значення-перемикачі (рушій, SSL) не секретні й не маскуються в тексті кроків.
        Assert.Equal("postgres require", scrubber.Scrub("postgres require"));
    }

    [Fact]
    public async Task HttpConnector_GenericCredential_HeaderMapWithPrefix_LegacyCredentialStillWorks()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var map = new JsonObject { ["$authorization"] = "Authorization", ["apiKey"] = "X-API-Key" };
        var basic = await worker.Credentials.CreateAsync("basic", "basic", new JsonObject { ["username"] = "u", ["password"] = "p-secret" }, null, default);
        var keyed = await worker.Credentials.CreateAsync("key", "apiKey", new JsonObject { ["apiKey"] = "k-secret-1" }, null, default);
        var legacy = await worker.Credentials.CreateAsync(
            "legacy", "mcp.http", new JsonObject { ["headers"] = new JsonObject { ["Authorization"] = "Bearer legacy-token" } }, null, default);
        var config = new JsonObject { ["url"] = "http://127.0.0.1:1/mcp", ["credentialMap"] = map };
        var servers = new[]
        {
            await AddServerAsync(worker, "http", config.DeepClone(), basic),
            await AddServerAsync(worker, "http", config.DeepClone(), keyed),
            await AddServerAsync(worker, "http", config.DeepClone(), legacy),
        };
        var scrubber = new SecretScrubber();

        foreach (var server in servers)
        {
            await worker.Servers.SyncAsync(Workflow(server.Id.ToString()), scrubber, default);
        }

        var headers = servers.Select(s => worker.Mcp.TryGetServer(s.Id.ToString(), out var c) ? c.Headers! : throw new InvalidOperationException()).ToList();
        Assert.Equal("Basic " + Convert.ToBase64String("u:p-secret"u8.ToArray()), headers[0]["Authorization"]);
        Assert.Equal("k-secret-1", headers[1]["X-API-Key"]);
        Assert.False(headers[1].ContainsKey("Authorization"));
        Assert.Equal("Bearer legacy-token", headers[2]["Authorization"]);
        Assert.Equal("***", scrubber.Scrub(headers[0]["Authorization"]));
    }

    [Theory]
    [InlineData("http", """{ "command": "x" }""", "url")]
    [InlineData("stdio", """{ "url": "http://x/mcp" }""", "command")]
    [InlineData("pigeon", "{}", "транспорт")]
    [InlineData("http", "[1]", "config")]
    public async Task InvalidConfig_Throws(string transport, string config, string expected)
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var server = await AddServerAsync(worker, transport, JsonNode.Parse(config)!);

        var ex = await Assert.ThrowsAsync<ConnectorConfigurationException>(
            () => worker.Servers.SyncAsync(Workflow(server.Id.ToString()), new SecretScrubber(), default));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferencedServers_SkipsExpressionsAndNonMcpNodes_IncludesAgentTools()
    {
        var workflow = WorkflowJson.Deserialize("""
            {
              "formatVersion": 1,
              "name": "x",
              "nodes": [
                { "id": "a", "name": "a", "type": "mcp.tool", "typeVersion": 1, "position": [0, 0], "parameters": { "serverId": "s1", "toolName": "t" } },
                { "id": "b", "name": "b", "type": "mcp.poll", "typeVersion": 1, "position": [0, 0], "parameters": { "serverId": "s2", "toolName": "t" } },
                { "id": "c", "name": "c", "type": "mcp.tool", "typeVersion": 1, "position": [0, 0], "parameters": { "serverId": "{{ $json.s }}", "toolName": "t" } },
                { "id": "d", "name": "d", "type": "core.set", "typeVersion": 1, "position": [0, 0], "parameters": { "serverId": "s3" } },
                { "id": "e", "name": "e", "type": "ai.agent", "typeVersion": 1, "position": [0, 0], "parameters": { "tools": [ { "serverId": "s4", "toolName": "t" }, { "serverId": "s1", "toolName": "u" }, { "serverId": "{{ $json.s }}", "toolName": "t" } ] } }
              ],
              "connections": []
            }
            """);

        Assert.Equal(["s1", "s2", "s4"], McpServerSync.ReferencedServers(workflow).Order());
    }

    [Fact]
    public void ReferencedVariables_TakesWholeVarsExpressionsOnly()
    {
        var workflow = WorkflowJson.Deserialize("""
            {
              "formatVersion": 1,
              "name": "x",
              "nodes": [
                { "id": "a", "name": "a", "type": "mcp.tool", "typeVersion": 1, "position": [0, 0], "parameters": { "serverId": "{{ $vars.tg }}", "toolName": "t" } },
                { "id": "b", "name": "b", "type": "mcp.tool", "typeVersion": 1, "position": [0, 0], "parameters": { "serverId": "{{$vars['other_server']}}", "toolName": "t" } },
                { "id": "c", "name": "c", "type": "mcp.tool", "typeVersion": 1, "position": [0, 0], "parameters": { "serverId": "{{ $vars.tg + $json.x }}", "toolName": "t" } },
                { "id": "e", "name": "e", "type": "ai.agent", "typeVersion": 1, "position": [0, 0], "parameters": { "tools": [ { "serverId": "{{ $vars.agent_tools }}", "toolName": "t" } ] } }
              ],
              "connections": []
            }
            """);

        Assert.Equal(["agent_tools", "other_server", "tg"], McpServerSync.ReferencedVariables(workflow).Order());
        Assert.Empty(McpServerSync.ReferencedServers(workflow));
    }

    [Fact]
    public async Task ServerFromVariable_IsRegistered()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var server = await AddServerAsync(worker, "http", new JsonObject { ["url"] = "http://127.0.0.1:1/mcp" });
        var key = "tg_" + Guid.NewGuid().ToString("N")[..8];
        await using (var db = worker.Db.CreateDbContext())
        {
            db.Variables.Add(new VariableEntity { Key = key, Value = server.Id.ToString(), UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await worker.Servers.SyncAsync(Workflow("{{ $vars." + key + " }}"), new SecretScrubber(), default);

        Assert.True(worker.Mcp.TryGetServer(server.Id.ToString(), out _));
    }

    private static async Task<McpServerEntity> AddServerAsync(
        WorkerHarness worker, string transport, JsonNode config, Guid? credentialId = null, string? toolsCache = null)
    {
        var row = new McpServerEntity
        {
            Id = Guid.NewGuid(),
            Name = "Тест",
            Transport = transport,
            Config = config.ToJsonString(),
            CredentialId = credentialId,
            Status = "new",
            ToolsCache = toolsCache,
        };
        await using var db = worker.Db.CreateDbContext();
        db.McpServers.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private static WorkflowDefinition Workflow(string serverId) => WorkflowJson.Deserialize($$"""
        {
          "formatVersion": 1,
          "name": "x",
          "nodes": [
            { "id": "a", "name": "a", "type": "mcp.tool", "typeVersion": 1, "position": [0, 0], "parameters": { "serverId": "{{serverId}}", "toolName": "echo" } }
          ],
          "connections": []
        }
        """);
}

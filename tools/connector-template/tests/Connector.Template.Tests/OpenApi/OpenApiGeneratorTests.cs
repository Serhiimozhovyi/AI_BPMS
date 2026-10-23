using System.Text.Json;
using Connector.Template.Generated;
using Connector.Template.OpenApi;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connector.Template.Tests.OpenApi;

/// <summary>
/// Генератор tools з OpenAPI (<c>Connector:Source = openapi:…</c>) на petstore-подібній специфікації: <c>tools/list</c>
/// містить tools операцій зі схемами, <c>tools/call</c> формує правильний запит до мок-REST, 4xx/5xx — <c>isError</c>.
/// </summary>
public sealed class OpenApiGeneratorTests : IAsyncLifetime
{
    private const string Token = "tok-openapi-secret-9";
    private const string Spec = "openapi:OpenApi/petstore.yaml";

    private RecordingRestApi _rest = null!;
    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync() => _rest = await RecordingRestApi.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            await d.DisposeAsync();
        }

        await _rest.DisposeAsync();
    }

    private async Task<McpClient> ConnectAsync(params string[] settings)
    {
        string[] args = [$"--Connector:BaseUrl={_rest.BaseUrl}", "--Connector:Auth:Type=bearer", "--Logging:LogLevel:Default=Warning", .. settings];
        var app = ConnectorHost.BuildHttp(args, "http://127.0.0.1:0", name => name == "CONNECTOR_TOKEN" ? Token : null);
        _disposables.Add(app);
        await app.StartAsync();
        var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(app.Urls.Single() + "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }));
        _disposables.Add(client);
        return client;
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string[] Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var required) ? required.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];

    [Fact]
    public async Task Tools_list_has_a_tool_per_operation_with_schemas()
    {
        var client = await ConnectAsync($"--Connector:Source={Spec}");
        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool);

        // Ручні tools шаблону лишаються; uploadPhoto (multipart) пропущено; без operationId — <method>_<path>.
        Assert.Equal(
            ["create_pet", "delete_pet", "get_item", "get_pet_by_id", "get_store_inventory", "list_items", "list_pets"],
            tools.Keys.Order());

        var list = tools["list_pets"];
        Assert.Equal("List pets\n\nReturns pets, optionally filtered by tags.", list.Description);
        Assert.True(list.Annotations?.ReadOnlyHint);
        var props = list.InputSchema.GetProperty("properties");
        Assert.Equal("integer", props.GetProperty("limit").GetProperty("type").GetString());
        Assert.Equal("Max number of pets.", props.GetProperty("limit").GetProperty("description").GetString());
        Assert.Equal("string", props.GetProperty("tags").GetProperty("items").GetProperty("type").GetString());
        Assert.True(props.TryGetProperty("X-Request-Id", out _));
        Assert.False(props.TryGetProperty("session", out _)); // cookie-параметри не передаємо
        Assert.Empty(Required(list.InputSchema));

        var get = tools["get_pet_by_id"];
        Assert.Equal("Get a pet by id", get.Description);
        Assert.Equal(["petId"], Required(get.InputSchema));
        Assert.Equal("Pet id.", get.InputSchema.GetProperty("properties").GetProperty("petId").GetProperty("description").GetString());

        var create = tools["create_pet"];
        Assert.False(create.Annotations?.ReadOnlyHint);
        Assert.Equal(["body"], Required(create.InputSchema));
        var body = create.InputSchema.GetProperty("properties").GetProperty("body");
        Assert.Equal(["name"], Required(body));
        Assert.Equal(["available", "sold"], body.GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        // nullable з OpenAPI 3.0 → тип-масив JSON Schema; $ref підставлено, цикл Owner.friends обірвано.
        Assert.Contains("null", body.GetProperty("properties").GetProperty("tag").GetProperty("type").EnumerateArray().Select(e => e.GetString()));
        var owner = body.GetProperty("properties").GetProperty("owner");
        Assert.Equal("See Owner.", owner.GetProperty("properties").GetProperty("friends").GetProperty("items").GetProperty("description").GetString());
        Assert.DoesNotContain("$ref", create.InputSchema.GetRawText(), StringComparison.Ordinal);

        Assert.True(tools["delete_pet"].Annotations?.DestructiveHint);
    }

    [Fact]
    public async Task Tools_call_builds_the_request_from_path_query_header_and_body()
    {
        var client = await ConnectAsync($"--Connector:Source={Spec}");

        var list = await client.CallToolAsync("list_pets", new Dictionary<string, object?>
        {
            ["limit"] = 10,
            ["tags"] = new[] { "dog", "кіт" },
            ["X-Request-Id"] = "req-1",
        });
        Assert.NotEqual(true, list.IsError);
        Assert.Equal(["Rex", "Mia"], list.StructuredContent!.Value.GetProperty("result").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        var request = _rest.Requests.Last();
        Assert.Equal("GET", request.Method);
        Assert.Equal("/api/pets?limit=10&tags=dog&tags=%D0%BA%D1%96%D1%82", request.Path);
        Assert.Equal("req-1", request.Headers["X-Request-Id"]);
        Assert.Equal($"Bearer {Token}", request.Headers["Authorization"]);

        var get = await client.CallToolAsync("get_pet_by_id", new Dictionary<string, object?> { ["petId"] = 1 });
        Assert.NotEqual(true, get.IsError);
        Assert.Equal("dog", get.StructuredContent!.Value.GetProperty("tag").GetString());
        Assert.Equal("/api/pets/1", _rest.Requests.Last().Path);

        var create = await client.CallToolAsync("create_pet", new Dictionary<string, object?>
        {
            ["body"] = new Dictionary<string, object?> { ["name"] = "Rex", ["status"] = "available" },
        });
        Assert.NotEqual(true, create.IsError);
        Assert.Equal(3, create.StructuredContent!.Value.GetProperty("id").GetInt32());
        request = _rest.Requests.Last();
        Assert.Equal(("POST", "/api/pets"), (request.Method, request.Path));
        Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
        using (var sent = JsonDocument.Parse(request.Body))
        {
            Assert.Equal("Rex", sent.RootElement.GetProperty("name").GetString());
            Assert.Equal("available", sent.RootElement.GetProperty("status").GetString());
        }

        var delete = await client.CallToolAsync("delete_pet", new Dictionary<string, object?> { ["petId"] = 1 });
        Assert.NotEqual(true, delete.IsError);
        Assert.Equal("HTTP 204", Text(delete));
        Assert.Equal(("DELETE", "/api/pets/1"), (_rest.Requests.Last().Method, _rest.Requests.Last().Path));

        var inventory = await client.CallToolAsync("get_store_inventory");
        Assert.Equal(5, inventory.StructuredContent!.Value.GetProperty("sold").GetInt32());
    }

    [Fact]
    public async Task Rest_4xx_and_5xx_are_isError_without_secrets()
    {
        var client = await ConnectAsync($"--Connector:Source={Spec}");

        var missing = await client.CallToolAsync("get_pet_by_id", new Dictionary<string, object?> { ["petId"] = 404 });
        Assert.True(missing.IsError);
        Assert.Contains("HTTP 404", Text(missing), StringComparison.Ordinal);

        _rest.FailWith = (500, $"boom, token {Token}");
        var failed = await client.CallToolAsync("list_pets");
        Assert.True(failed.IsError);
        Assert.Contains("HTTP 500", Text(failed), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(failed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_required_argument_is_isError_without_request()
    {
        var client = await ConnectAsync($"--Connector:Source={Spec}");

        var result = await client.CallToolAsync("get_pet_by_id");

        Assert.True(result.IsError);
        Assert.Contains("petId", Text(result), StringComparison.Ordinal);
        Assert.Empty(_rest.Requests);
    }

    [Fact]
    public async Task Include_and_exclude_filter_operations_by_tag_and_path()
    {
        var pets = await ConnectAsync($"--Connector:Source={Spec}", "--Connector:OpenApi:Include:0=tag:pets", "--Connector:OpenApi:Exclude:0=/pets/{petId}*");
        Assert.Equal(["create_pet", "get_item", "list_items", "list_pets"], (await pets.ListToolsAsync()).Select(t => t.Name).Order());

        var store = await ConnectAsync($"--Connector:Source={Spec}", "--Connector:OpenApi:Include:0=/store/*");
        Assert.Equal(["get_item", "get_store_inventory", "list_items"], (await store.ListToolsAsync()).Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Spec_is_loaded_from_url()
    {
        var client = await ConnectAsync($"--Connector:Source=openapi:{_rest.Address}/openapi.yaml", "--Connector:OpenApi:Include:0=tag:store");

        var result = await client.CallToolAsync("get_store_inventory");

        Assert.Equal(2, result.StructuredContent!.Value.GetProperty("available").GetInt32());
    }

    [Fact]
    public async Task Json_spec_file_works_and_a_name_clash_with_a_manual_tool_fails_start()
    {
        var file = Path.Combine(Path.GetTempPath(), $"openapi-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, """
            {
              "openapi": "3.1.0",
              "info": { "title": "t", "version": "1" },
              "paths": {
                "/items": { "get": { "operationId": "listItems", "responses": { "200": { "description": "ok" } } } },
                "/pets": { "get": { "operationId": "list-pets", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """);
        try
        {
            var client = await ConnectAsync($"--Connector:Source=openapi:{file}", "--Connector:OpenApi:Exclude:0=/items");
            Assert.Contains("list_pets", (await client.ListToolsAsync()).Select(t => t.Name));

            var clash = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync($"--Connector:Source=openapi:{file}"));
            Assert.Contains("list_items", clash.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Stdio_transport_serves_generated_tools()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = dotnet,
            Arguments = [typeof(ConnectorHost).Assembly.Location],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["Connector__BaseUrl"] = _rest.BaseUrl.ToString(),
                ["Connector__Source"] = $"openapi:{Path.Combine(AppContext.BaseDirectory, "OpenApi", "petstore.yaml")}",
            },
        }));

        var result = await client.CallToolAsync("get_pet_by_id", new Dictionary<string, object?> { ["petId"] = 1 });

        Assert.Equal("Rex", result.StructuredContent!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Missing_spec_or_unknown_source_fails_start()
    {
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync("--Connector:Source=openapi:no-such.yaml"));
        Assert.Contains("no-such.yaml", missing.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<OptionsValidationException>(() => ConnectAsync("--Connector:Source=wsdl:x"));
    }

    [Theory]
    [InlineData("findPetsByStatus", "find_pets_by_status")]
    [InlineData("getHTTPResponse", "get_http_response")]
    [InlineData("list-pets", "list_pets")]
    [InlineData("get_/pets/{petId}", "get_pets_pet_id")]
    [InlineData("v2.users.get", "v2_users_get")]
    [InlineData("Catalog_Контрагенты", "catalog_kontragenty")]
    [InlineData("Document_ЗаказКлиента", "document_zakaz_klienta")]
    public void Tool_name_is_snake_case(string source, string expected) => Assert.Equal(expected, ToolNaming.ToolName(source));
}

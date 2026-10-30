using System.Text;
using System.Text.Json.Nodes;
using OpenBP.Application.Connectors;
using OpenBP.Application.Credentials;
using OpenBP.Application.Errors;
using OpenBP.Mcp.Client;

namespace OpenBP.Application.Tests;

/// <summary>F3-93: загальні типи облікових даних, відображення їхніх полів на змінні/заголовки, маніфест з type/map/settings.</summary>
public sealed class CredentialTypesTests
{
    private const string GoogleKey = """{ "type": "service_account", "client_email": "bot@p.iam.gserviceaccount.com", "private_key": "-----BEGIN PRIVATE KEY-----x" }""";

    public static TheoryData<string, string> ValidData => new()
    {
        { CredentialTypes.TelegramBot, """{ "token": "123:abc" }""" },
        { CredentialTypes.ApiKey, """{ "apiKey": "k", "name": "X-Key", "in": "query" }""" },
        { CredentialTypes.ApiKey, """{ "apiKey": "k" }""" },
        { CredentialTypes.Bearer, """{ "token": "t" }""" },
        { CredentialTypes.Basic, """{ "username": "u", "password": "p" }""" },
        { CredentialTypes.Basic, """{ "username": "u" }""" },
        { CredentialTypes.OAuth2ClientCredentials, """{ "tokenUrl": "https://sso/token", "clientId": "c", "clientSecret": "s", "scopes": "a b" }""" },
        { CredentialTypes.Database, """{ "engine": "postgres", "host": "db", "port": "5432", "database": "crm", "username": "u", "password": "p", "ssl": "require" }""" },
        { CredentialTypes.Database, """{ "engine": "mysql", "host": "db", "database": "crm", "username": "u" }""" },
        { CredentialTypes.GoogleServiceAccount, new JsonObject { ["serviceAccountJson"] = GoogleKey }.ToJsonString() },
    };

    public static TheoryData<string, string, string> InvalidData => new()
    {
        { CredentialTypes.TelegramBot, """{ }""", "token" },
        { CredentialTypes.TelegramBot, """{ "token": "" }""", "token" },
        { CredentialTypes.ApiKey, """{ "apiKey": "k", "in": "cookie" }""", "in" },
        { CredentialTypes.Bearer, """{ "token": "t", "extra": "x" }""", "extra" },
        { CredentialTypes.Basic, """{ "password": "p" }""", "username" },
        { CredentialTypes.OAuth2ClientCredentials, """{ "tokenUrl": "not a url", "clientId": "c", "clientSecret": "s" }""", "tokenUrl" },
        { CredentialTypes.OAuth2ClientCredentials, """{ "tokenUrl": "https://sso/token", "clientId": "c" }""", "clientSecret" },
        { CredentialTypes.Database, """{ "engine": "oracle", "host": "db", "database": "crm", "username": "u" }""", "engine" },
        { CredentialTypes.Database, """{ "engine": "postgres", "host": "db", "port": "70000", "database": "crm", "username": "u" }""", "port" },
        { CredentialTypes.Database, """{ "engine": "postgres", "host": "db", "database": "crm", "username": "u", "ssl": "maybe" }""", "ssl" },
        { CredentialTypes.GoogleServiceAccount, """{ "serviceAccountJson": "{ not json" }""", "serviceAccountJson" },
        { CredentialTypes.GoogleServiceAccount, """{ "serviceAccountJson": "{ \"type\": \"service_account\" }" }""", "serviceAccountJson" },
    };

    [Theory]
    [MemberData(nameof(ValidData))]
    public void Valid_data_of_every_generic_type_passes(string type, string data) =>
        CredentialTypes.Validate(type, (JsonObject)JsonNode.Parse(data)!);

    [Theory]
    [MemberData(nameof(InvalidData))]
    public void Invalid_data_names_the_field(string type, string data, string field)
    {
        var error = Assert.Throws<CredentialValidationException>(() => CredentialTypes.Validate(type, (JsonObject)JsonNode.Parse(data)!));
        Assert.Contains(error.Errors, e => e.Field == field);
        // Значення (зокрема ключ Google) не потрапляє в повідомлення.
        Assert.All(error.Errors, e => Assert.DoesNotContain("PRIVATE KEY", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Generic_types_have_described_fields_and_secrets_are_write_only()
    {
        foreach (var name in new[] { CredentialTypes.TelegramBot, CredentialTypes.ApiKey, CredentialTypes.Bearer, CredentialTypes.Basic, CredentialTypes.OAuth2ClientCredentials, CredentialTypes.Database, CredentialTypes.GoogleServiceAccount })
        {
            var type = CredentialTypes.Find(name);
            Assert.NotNull(type);
            Assert.False(string.IsNullOrWhiteSpace(type.DisplayName));
            Assert.DoesNotContain(type.Fields, f => f.Kind == CredentialFieldKind.SecretMap);
            var properties = type.Schema["properties"]!.AsObject();
            foreach (var field in type.Fields.Where(f => f.Kind == CredentialFieldKind.Secret))
            {
                Assert.True((bool)properties[field.Name]!["writeOnly"]!, $"{name}.{field.Name}");
            }
        }

        var database = CredentialTypes.Find(CredentialTypes.Database)!.Schema["properties"]!;
        Assert.Equal(["postgres", "mysql"], database["engine"]!["enum"]!.AsArray().Select(v => (string)v!));
        Assert.Equal("db.example.com", (string)database["host"]!["examples"]![0]!);
        Assert.False(string.IsNullOrWhiteSpace((string?)CredentialTypes.Find(CredentialTypes.TelegramBot)!.Schema["properties"]!["token"]!["description"]));
        // Поля, якими керує AI_BPMS, у формі не показуються.
        Assert.Null(CredentialTypes.Find(CredentialTypes.OAuth2ClientCredentials)!.Schema["properties"]!["accessToken"]);
    }

    [Fact]
    public void Map_builds_environment_and_headers_with_prefix()
    {
        var credential = new JsonObject { ["clientId"] = "c-1", ["clientSecret"] = "s-1", ["accessToken"] = "at-1", ["tokenType"] = "Bearer" };
        var env = ConnectorCredentialMap.Environment(new Dictionary<string, string> { ["clientId"] = "KIT_CLIENT_ID", ["clientSecret"] = "KIT_CLIENT_SECRET", ["scopes"] = "KIT_SCOPES" }, credential)
            .ToDictionary();
        Assert.Equal(new Dictionary<string, string> { ["KIT_CLIENT_ID"] = "c-1", ["KIT_CLIENT_SECRET"] = "s-1" }, env);

        var headers = ConnectorCredentialMap.Headers(new Dictionary<string, string> { ["$accessToken"] = "Authorization: Bearer", ["clientId"] = "X-Client" }, credential).ToDictionary();
        Assert.Equal("Bearer at-1", headers["Authorization"]);
        Assert.Equal("c-1", headers["X-Client"]);

        Assert.Equal("Bearer t-2", ConnectorCredentialMap.Resolve("$authorization", new JsonObject { ["token"] = "t-2" }));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("u:p")), ConnectorCredentialMap.Resolve("$authorization", new JsonObject { ["username"] = "u", ["password"] = "p" }));
        Assert.Throws<InvalidOperationException>(() => ConnectorCredentialMap.Environment(new Dictionary<string, string> { ["token"] = "T" }, new JsonObject { ["token"] = new JsonObject() }).ToList());
    }

    [Fact]
    public void Connection_string_is_built_and_escaped_per_engine()
    {
        var postgres = ConnectorCredentialMap.BuildConnectionString(new JsonObject
        {
            ["engine"] = "postgres",
            ["host"] = "db",
            ["port"] = "5433",
            ["database"] = "crm",
            ["username"] = "reader",
            ["password"] = "p;w=d",
            ["ssl"] = "require",
        })!;
        Assert.Contains("host=db", postgres, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("port=5433", postgres, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("username=reader", postgres, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ssl mode=Require", postgres, StringComparison.OrdinalIgnoreCase);
        var parsed = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = postgres };
        Assert.Equal("p;w=d", parsed["password"]);

        var mysql = ConnectorCredentialMap.BuildConnectionString(new JsonObject { ["engine"] = "mysql", ["host"] = "db", ["database"] = "crm", ["username"] = "u", ["ssl"] = "disable" })!;
        Assert.Contains("server=db", mysql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user id=u", mysql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sslmode=None", mysql, StringComparison.OrdinalIgnoreCase);

        Assert.Null(ConnectorCredentialMap.BuildConnectionString(new JsonObject { ["env"] = new JsonObject { ["SQL_CONNECTION_STRING"] = "x" } }));
    }

    [Fact]
    public void Legacy_env_credential_gives_nothing_through_the_map()
    {
        var legacy = new JsonObject { ["env"] = new JsonObject { ["TELEGRAM_BOT_TOKEN"] = "123:abc" } };
        Assert.Empty(ConnectorCredentialMap.Environment(new Dictionary<string, string> { ["token"] = "TELEGRAM_BOT_TOKEN" }, legacy));
    }

    [Fact]
    public void Scrubber_data_keeps_secrets_but_not_switch_values()
    {
        var data = CredentialTypes.ForScrubber(new JsonObject { ["engine"] = "postgres", ["ssl"] = "require", ["port"] = "5432", ["password"] = "p-1", ["host"] = "db" })!;
        Assert.Equal(["password", "host"], data.Select(p => p.Key));
    }

    [Fact]
    public void Manifest_with_generic_type_maps_fields_and_settings_into_config()
    {
        var manifest = ConnectorCatalog.Parse(Manifest("""
            "credential": { "type": "oauth2.clientCredentials", "map": { "clientSecret": "KIT_CLIENT_SECRET", "$accessToken": "KIT_TOKEN" }, "headerMap": { "$accessToken": "Authorization: Bearer" } },
            "settings": [ { "name": "baseUrl", "title": "Адреса OData", "required": true, "env": "Connector__BaseUrl", "header": "X-Base" } ]
            """), "test");

        var stdio = manifest.ConfigFor(McpTransportKind.Stdio, new Dictionary<string, string> { ["baseUrl"] = " https://crm/odata/ " });
        Assert.Equal("https://crm/odata/", stdio.Environment!["Connector__BaseUrl"]);
        Assert.Equal("MODE", Assert.Single(stdio.Environment, p => p.Value == "test").Key);
        Assert.Equal("KIT_CLIENT_SECRET", stdio.CredentialMap!["clientSecret"]);
        Assert.Equal("test", stdio.Catalog);

        var http = manifest.ConfigFor(McpTransportKind.Http, new Dictionary<string, string> { ["baseUrl"] = "https://crm/odata/" });
        Assert.Equal("https://crm/odata/", http.Headers!["X-Base"]);
        Assert.Equal("Authorization: Bearer", http.CredentialMap!["$accessToken"]);

        Assert.Throws<ArgumentException>(() => manifest.ConfigFor(McpTransportKind.Stdio, new Dictionary<string, string> { ["unknown"] = "x" }));
        Assert.Null(manifest.ConfigFor(McpTransportKind.Stdio, new Dictionary<string, string> { ["baseUrl"] = " " }).Environment!.GetValueOrDefault("Connector__BaseUrl"));
    }

    [Theory]
    [InlineData(""" "credential": { "type": "mcp.stdio", "map": { "env": "X" } } """, "не загальний тип")]
    [InlineData(""" "credential": { "type": "nope", "map": { "a": "X" } } """, "не загальний тип")]
    [InlineData(""" "credential": { "map": { "token": "X" } } """, "потребують credential.type")]
    [InlineData(""" "credential": { "type": "bearer" } """, "map порожній")]
    [InlineData(""" "credential": { "type": "bearer", "map": { "password": "X" } } """, "не поле типу")]
    [InlineData(""" "credential": { "type": "bearer", "map": { "$connectionString": "X" } } """, "не поле типу")]
    [InlineData(""" "credential": { "type": "bearer", "map": { "token": "BAD-NAME" } } """, "змінної середовища")]
    [InlineData(""" "credential": { "type": "bearer", "map": { "token": "T" }, "headerMap": { "token": "Bad Header: x" } } """, "заголовок")]
    [InlineData(""" "settings": [ { "name": "a", "title": "A" } ] """, "ціль env")]
    [InlineData(""" "settings": [ { "name": "a", "title": "A", "env": "A" }, { "name": "a", "title": "B", "env": "B" } ] """, "повторюється")]
    [InlineData(""" "settings": [ { "name": "a-b", "title": "A", "env": "A" } ] """, "name")]
    [InlineData(""" "settings": [ { "name": "a", "title": " ", "env": "A" } ] """, "title")]
    public void Invalid_credential_or_settings_are_rejected(string section, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ConnectorCatalog.Parse(Manifest(section), "test"));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    private static string Manifest(string section) => $$"""
        { "id": "test", "name": "Test", "description": "", "transport": "stdio",
          "stdio": { "command": "x", "environment": { "MODE": "test" } }, "http": { "url": "http://test/mcp" },
          {{section}} }
        """;
}

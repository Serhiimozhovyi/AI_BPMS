extern alias worker;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using Testcontainers.PostgreSql;
using worker::OpenBP.Worker;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>
/// Хелпер тестів <c>/mcp</c> (F3-03…F3-06, F3-17): Api над <c>postgres:16</c> у Testcontainers і MCP-клієнт SDK,
/// що ходить у нього через <see cref="WebApplicationFactory{TEntryPoint}"/> з API-ключем користувача потрібної ролі.
/// </summary>
public sealed class McpServerFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16").Build();
    private readonly SemaphoreSlim _workerLock = new(1, 1);
    private IHost? _worker;

    static McpServerFactory()
    {
        // Ключ credentials Api читає зі змінної середовища — як у продукті.
        Environment.SetEnvironmentVariable(CredentialEncryptionKey.EnvironmentVariable, EncryptionKey);
    }

    /// <summary>Ключ шифрування credentials тестового хоста (base64, 32 байти).</summary>
    public static string EncryptionKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(CredentialEncryptionKey.KeySize));

    /// <summary>Корінь репозиторію (тека з <c>OpenBP.sln</c>) — для прикладів з <c>examples/</c>.</summary>
    public static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("OpenBP.sln не знайдено.");
        }
    }

    /// <summary>Адреса MCP-ендпойнта тестового хоста.</summary>
    public Uri McpEndpoint => new(Server.BaseAddress, "/mcp");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        _ = Server;
    }

    public new async Task DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync();
            _worker.Dispose();
        }

        await base.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Запускає (один раз на фікстуру) воркер у процесі тестів над тією самою БД — ті самі сервіси, що в
    /// <c>OpenBP.Worker</c>, з коротким інтервалом опитування черги.
    /// </summary>
    public async Task EnsureWorkerAsync()
    {
        await _workerLock.WaitAsync();
        try
        {
            if (_worker is not null)
            {
                return;
            }

            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _container.GetConnectionString(),
                ["Worker:PollInterval"] = "00:00:00.050",
                ["Worker:HeartbeatInterval"] = "00:00:00.500",
                ["Logging:LogLevel:Default"] = "Warning",
            });
            builder.Services.AddOpenBpWorker(builder.Configuration);
            var host = builder.Build();
            await host.StartAsync();
            _worker = host;
        }
        finally
        {
            _workerLock.Release();
        }
    }

    /// <summary>
    /// Конектор stdio FakeServer (<c>--mock 1c|telegram</c> або загальні tools) з необов'язковим credential (<c>{ "env": { … } }</c>):
    /// через REST адміністратором, як у продукті, — тож discovery виконується і tools потрапляють у реєстр Api, за яким
    /// валідуються вузли <c>mcp.*</c>.
    /// </summary>
    /// <param name="mock"><c>1c</c> або <c>telegram</c>; <c>null</c> — загальні tools FakeServer (<c>echo</c>, <c>add</c>, <c>fail</c>…).</param>
    /// <param name="credentialId">Credential зі змінними середовища.</param>
    public async Task<Guid> CreateMockConnectorAsync(string? mock, Guid? credentialId = null)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        var (_, key) = await CreateApiKeyAsync(UserRole.Admin);
        using var http = CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        var response = await http.PostAsJsonAsync("/api/v1/connectors", new
        {
            name = $"{mock ?? "fake"} (мок) {Guid.NewGuid():N}",
            transport = "stdio",
            config = new
            {
                command = dotnet,
                arguments = mock is null
                    ? [Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll")]
                    : new[] { Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll"), "--mock", mock },
            },
            credentialId,
        });
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        if ((string?)created["summary"]!["status"] != "ok")
        {
            throw new InvalidOperationException($"Мок {mock} не пройшов discovery: {created.ToJsonString()}");
        }

        return Guid.Parse((string)created["summary"]!["id"]!);
    }

    public OpenBpDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    /// <summary>Користувач з роллю <paramref name="role"/> і його API-ключ (<c>obp_…</c>).</summary>
    /// <param name="role">Роль.</param>
    public async Task<(UserEntity User, ApiKeyCreated Key)> CreateApiKeyAsync(UserRole role)
    {
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = $"{UserRoles.ToValue(role)}-{Guid.NewGuid():N}@openbp.test",
            PasswordHash = AuthService.HashPassword("User-pass-1"),
            Role = UserRoles.ToValue(role),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await using var db = CreateDbContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var key = await new ApiKeyService(db, new KeyOwner(user), TimeProvider.System).CreateAsync("mcp tests", CancellationToken.None);
        return (user, key);
    }

    /// <summary>MCP-клієнт SDK з <c>Authorization: Bearer <paramref name="apiKey"/></c> (без ключа — <c>null</c>).</summary>
    /// <param name="apiKey">API-ключ.</param>
    public Task<McpClient> ConnectAsync(string? apiKey)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = McpEndpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = apiKey is null ? null : new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
        };
        var transport = new HttpClientTransport(options, CreateClient(), NullLoggerFactory.Instance, ownsHttpClient: true);
        return McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    /// <summary>Клієнт MCP з новим ключем користувача ролі <paramref name="role"/>.</summary>
    /// <param name="role">Роль.</param>
    public async Task<McpClient> ConnectAsync(UserRole role) => await ConnectAsync((await CreateApiKeyAsync(role)).Key.Key);

    /// <summary>Зашифрований credential тим самим ключем, що й у хоста.</summary>
    /// <param name="data">Секретні дані.</param>
    public Task<Guid> CreateCredentialAsync(JsonObject data)
    {
        var factory = new PooledDbContextFactory<OpenBpDbContext>(
            new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(_container.GetConnectionString()).Options);
        var store = new CredentialStore(factory, CredentialEncryptionKey.FromBase64(EncryptionKey));
        return store.CreateAsync("mcp test", "httpHeaders", data, createdBy: null, CancellationToken.None);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Default", _container.GetConnectionString());

    private sealed class KeyOwner(UserEntity user) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => user.Id;

        public string? Email => user.Email;

        public UserRole? Role => UserRoles.TryParse(user.Role);
    }
}

[CollectionDefinition(Name)]
public sealed class McpServerCollection : ICollectionFixture<McpServerFactory>
{
    public const string Name = "mcp-server";
}

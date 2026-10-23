using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using Testcontainers.PostgreSql;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>
/// Хелпер тестів <c>/mcp</c> (F3-03…F3-06, F3-17): Api над <c>postgres:16</c> у Testcontainers і MCP-клієнт SDK,
/// що ходить у нього через <see cref="WebApplicationFactory{TEntryPoint}"/> з API-ключем користувача потрібної ролі.
/// </summary>
public sealed class McpServerFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16").Build();

    static McpServerFactory()
    {
        // Ключ credentials Api читає зі змінної середовища — як у продукті.
        Environment.SetEnvironmentVariable(CredentialEncryptionKey.EnvironmentVariable, EncryptionKey);
    }

    /// <summary>Ключ шифрування credentials тестового хоста (base64, 32 байти).</summary>
    public static string EncryptionKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(CredentialEncryptionKey.KeySize));

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
        await base.DisposeAsync();
        await _container.DisposeAsync();
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

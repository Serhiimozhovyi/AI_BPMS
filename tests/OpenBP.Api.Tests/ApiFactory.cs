using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using Testcontainers.PostgreSql;

namespace OpenBP.Api.Tests;

/// <summary>
/// Api над <c>postgres:16</c> у Testcontainers: БД мігрується перед стартом хоста, перший адміністратор
/// створюється з <see cref="AdminEmail"/>/<see cref="AdminPassword"/>.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@openbp.test";
    public const string AdminPassword = "Admin-pass-1";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16").Build();

    static ApiFactory()
    {
        // Ключ credentials (auth webhook-ів) Api читає зі змінної середовища — як у продукті.
        Environment.SetEnvironmentVariable(CredentialEncryptionKey.EnvironmentVariable, EncryptionKey);
    }

    /// <summary>Ключ шифрування credentials тестового хоста (base64, 32 байти).</summary>
    public static string EncryptionKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(CredentialEncryptionKey.KeySize));

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// Зовнішній каталог конекторів (F6-16, <c>OPENBP_CONNECTOR_CATALOG_URL</c>): запис <c>fake</c> запускає FakeServer
    /// через stdio, щоб встановлення з каталогу проходило discovery.
    /// </summary>
    public static string ExternalCatalogPath { get; } = WriteExternalCatalog();

    private static string WriteExternalCatalog()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h && File.Exists(h) ? h : "dotnet";
        var path = Path.Combine(Path.GetTempPath(), $"openbp-catalog-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            connectors = new[]
            {
                new
                {
                    id = "fake",
                    name = "FakeServer",
                    description = "Мок-сервер тестів.",
                    transport = "stdio",
                    stdio = new { command = host, arguments = new[] { Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll") } },
                },
            },
        }));
        return path;
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        // Старт хоста до будь-якого тесту: перший адміністратор створюється, лише поки users порожня.
        _ = Server;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _container.DisposeAsync();
    }

    public OpenBpDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>Створює зашифрований credential тим самим ключем, що й у хоста.</summary>
    public Task<Guid> CreateCredentialAsync(string type, System.Text.Json.Nodes.JsonObject data)
    {
        var factory = new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<OpenBpDbContext>(
            new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(ConnectionString).Options);
        var store = new CredentialStore(factory, CredentialEncryptionKey.FromBase64(EncryptionKey));
        return store.CreateAsync($"test {type}", type, data, createdBy: null, CancellationToken.None);
    }

    /// <summary>Додає користувача з роллю напряму в БД.</summary>
    public async Task<UserEntity> CreateUserAsync(UserRole role, string password = "User-pass-1")
    {
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = $"{UserRoles.ToValue(role)}-{Guid.NewGuid():N}@openbp.test",
            PasswordHash = AuthService.HashPassword(password),
            Role = UserRoles.ToValue(role),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await using var db = CreateDbContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("OPENBP_ADMIN_EMAIL", AdminEmail);
        builder.UseSetting("OPENBP_ADMIN_PASSWORD", AdminPassword);
        // Розсилку сповіщень тести викликають самі (F6-07): фоновий job забирав би їхні події.
        builder.UseSetting("OPENBP_NOTIFICATIONS_INTERVAL_SEC", "0");
        builder.UseSetting("OPENBP_CONNECTOR_CATALOG_URL", ExternalCatalogPath);
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

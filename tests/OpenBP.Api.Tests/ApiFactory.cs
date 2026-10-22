using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Data;
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

    public string ConnectionString => _container.GetConnectionString();

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
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

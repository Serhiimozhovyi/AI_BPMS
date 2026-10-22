using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace OpenBP.Data.Tests;

/// <summary>Піднімає <c>postgres:16</c> у Testcontainers один раз на колекцію тестів.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16").Build();
    private readonly SemaphoreSlim _migratedLock = new(1, 1);
    private string? _migrated;

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        _migratedLock.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>Нова порожня БД у контейнері; повертає рядок підключення до неї.</summary>
    public async Task<string> NewDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(ConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>Спільна для колекції БД із застосованими міграціями (тести пишуть у неї з унікальними id).</summary>
    public async Task<string> MigratedDatabaseAsync()
    {
        await _migratedLock.WaitAsync();
        try
        {
            if (_migrated is null)
            {
                var cs = await NewDatabaseAsync();
                await using var db = CreateContext(cs);
                await db.Database.MigrateAsync();
                _migrated = cs;
            }

            return _migrated;
        }
        finally
        {
            _migratedLock.Release();
        }
    }

    public static DbContextOptions<OpenBpDbContext> Options(string cs) =>
        new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(cs).Options;

    public static OpenBpDbContext CreateContext(string cs) => new(Options(cs));
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

using System.Data.Common;
using MySqlConnector;
using Npgsql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace OpenBP.Connectors.Sql.Tests;

/// <summary>Справжня база в контейнері: рядок підключення, провайдер і виконання SQL напряму (підготовка даних).</summary>
public abstract class DatabaseFixture : IAsyncLifetime
{
    /// <summary>Пароль користувача бази: у текстах помилок конектора його бути не повинно.</summary>
    public const string Password = "Sql-Conn3ctor-Pw";

    public abstract SqlProvider Provider { get; }

    public abstract string ConnectionString { get; }

    public abstract Task InitializeAsync();

    public abstract Task DisposeAsync();

    /// <summary>Таблиці для тестів читання; тести запису створюють власні таблиці через <see cref="ExecAsync"/>.</summary>
    protected abstract string SeedSql { get; }

    protected abstract DbConnection Open();

    public async Task ExecAsync(string sql)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    protected async Task SeedAsync()
    {
        foreach (var statement in SeedSql.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await ExecAsync(statement);
        }
    }
}

public sealed class PostgresFixture : DatabaseFixture
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").WithPassword(Password).Build();

    public override SqlProvider Provider => SqlProvider.Postgres;

    public override string ConnectionString => _container.GetConnectionString();

    public override async Task InitializeAsync()
    {
        await _container.StartAsync();
        await SeedAsync();
    }

    public override Task DisposeAsync() => _container.DisposeAsync().AsTask();

    protected override DbConnection Open() => new NpgsqlConnection(ConnectionString);

    protected override string SeedSql => """
        create table customers (
          id serial primary key, name text not null, email text unique, active boolean not null default true,
          meta jsonb, balance numeric(10,2), born date, created_at timestamptz not null default now(),
          updated_at timestamptz not null default now());
        create view active_customers as select id, name from customers where active;
        insert into customers (name, email, active, meta, balance, born, updated_at) values
          ('Петренко', 'p@firma.ua', true, '{"tier":"gold","tags":["a"]}', 1200.50, '1990-05-01', '2026-10-01T08:00:00Z'),
          ('Іваненко', 'i@firma.ua', false, null, 0, null, '2026-10-02T08:00:00Z'),
          ('Коваль', 'k@firma.ua', true, '{"tier":"silver"}', 99.99, '1985-12-31', '2026-10-03T08:00:00Z');
        create schema crm;
        create table crm.customers (id int primary key, title text);
        create table logs (message text)
        """;
}

public sealed class MySqlFixture : DatabaseFixture
{
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.0").WithPassword(Password).Build();

    public override SqlProvider Provider => SqlProvider.MySql;

    public override string ConnectionString => _container.GetConnectionString();

    public override async Task InitializeAsync()
    {
        await _container.StartAsync();
        await SeedAsync();
    }

    public override Task DisposeAsync() => _container.DisposeAsync().AsTask();

    protected override DbConnection Open() => new MySqlConnection(ConnectionString);

    protected override string SeedSql => """
        create table customers (
          id int auto_increment primary key, name varchar(100) not null, email varchar(200) unique,
          active tinyint(1) not null default 1, meta json, balance decimal(10,2), born date,
          created_at datetime(6) not null default current_timestamp(6), updated_at datetime(6) not null default current_timestamp(6));
        create view active_customers as select id, name from customers where active;
        insert into customers (name, email, active, meta, balance, born, updated_at) values
          ('Петренко', 'p@firma.ua', 1, '{"tier":"gold","tags":["a"]}', 1200.50, '1990-05-01', '2026-10-01 08:00:00'),
          ('Іваненко', 'i@firma.ua', 0, null, 0, null, '2026-10-02 08:00:00'),
          ('Коваль', 'k@firma.ua', 1, '{"tier":"silver"}', 99.99, '1985-12-31', '2026-10-03 08:00:00');
        create table logs (message text)
        """;
}

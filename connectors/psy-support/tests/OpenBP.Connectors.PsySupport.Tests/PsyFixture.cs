using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenBP.Connectors.PsySupport.Data;
using OpenBP.Connectors.PsySupport.Domain;
using OpenBP.Connectors.PsySupport.Hosting;
using Testcontainers.PostgreSql;

namespace OpenBP.Connectors.PsySupport.Tests;

/// <summary>Один <c>postgres:16</c> на всі тести; кожен тест отримує нову БД з міграціями.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> NewDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(ConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>Керований «зараз» для детермінованих правил часу.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Журнал у пам'яті — для перевірки, що в логах немає персональних даних.</summary>
public sealed class ListLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(ListLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines)
            {
                owner.Lines.Add($"{category}: {formatter(state, exception)} {exception}");
            }
        }
    }
}

/// <summary>Сервіси застосунку поверх окремої БД і <see cref="TestClock"/>.</summary>
public sealed class PsyApp : IAsyncDisposable
{
    // Середа, 12.11.2025, 09:00 за Києвом.
    public static readonly DateTimeOffset Start = new(2025, 11, 12, 7, 0, 0, TimeSpan.Zero);

    private PsyApp(ServiceProvider services, TestClock clock, string connectionString)
    {
        Services = services;
        Clock = clock;
        ConnectionString = connectionString;
    }

    public ServiceProvider Services { get; }

    public TestClock Clock { get; }

    public string ConnectionString { get; }

    public ListLoggerProvider Logs { get; } = new();

    public static async Task<PsyApp> CreateAsync(PostgresFixture pg, ListLoggerProvider? logs = null)
    {
        var cs = await pg.NewDatabaseAsync();
        var clock = new TestClock(Start);
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            if (logs is not null)
            {
                b.AddProvider(logs);
            }
        });
        PsyHost.AddPsy(services, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:PsySupport"] = cs }).Build(), _ => null);
        services.AddSingleton<TimeProvider>(clock);
        var provider = services.BuildServiceProvider();
        await PsyHost.InitializeAsync(provider);
        return new PsyApp(provider, clock, cs);
    }

    public AsyncServiceScope Scope() => Services.CreateAsyncScope();

    public async Task<T> With<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Scope();
        return await action(scope.ServiceProvider);
    }

    public Task<T> With<TService, T>(Func<TService, Task<T>> action)
        where TService : notnull => With(sp => action(sp.GetRequiredService<TService>()));

    public async Task Db(Func<PsyDbContext, Task> action)
    {
        await using var scope = Scope();
        await action(scope.ServiceProvider.GetRequiredService<PsyDbContext>());
    }

    public async Task<long> PsychologistAsync(string name = "Олена Психолог", string? phone = "380671234567", long? telegramId = null, bool active = true, string? email = "olena@example.com")
    {
        long id = 0;
        await Db(async db =>
        {
            var p = new PsychologistEntity { Name = name, Phone = phone, TelegramId = telegramId, Active = active, Email = email, CreatedAt = Clock.Now };
            db.Psychologists.Add(p);
            await db.SaveChangesAsync();
            id = p.Id;
        });
        return id;
    }

    public Task UserAsync(long telegramId, string name = "Марія", string? phone = null) =>
        Db(async db =>
        {
            db.Users.Add(new UserEntity { TelegramId = telegramId, Name = name, Phone = phone ?? $"38050{telegramId % 10000000:D7}", RegisteredAt = Clock.Now });
            await db.SaveChangesAsync();
        });

    public async Task<long> SlotAsync(long psychologistId, TimeSpan fromNow)
    {
        long id = 0;
        await Db(async db =>
        {
            var s = new SlotEntity { PsychologistId = psychologistId, StartsAt = Clock.Now + fromNow, DurationMin = 50, CreatedAt = Clock.Now };
            db.Slots.Add(s);
            await db.SaveChangesAsync();
            id = s.Id;
        });
        return id;
    }

    public async Task<long> EventAsync(int capacity, TimeSpan fromNow, string? notice = null)
    {
        long id = 0;
        await Db(async db =>
        {
            var e = new EventEntity { Title = "Зустріч підтримки", StartsAt = Clock.Now + fromNow, Capacity = capacity, Status = EventStatuses.Open, Notice = notice };
            db.Events.Add(e);
            await db.SaveChangesAsync();
            id = e.Id;
        });
        return id;
    }

    public async Task<long> SessionAsync(long psychologistId, int capacity, TimeSpan fromNow)
    {
        long id = 0;
        await Db(async db =>
        {
            var s = new BstSessionEntity { PsychologistId = psychologistId, StartsAt = Clock.Now + fromNow, Capacity = capacity, Format = "онлайн" };
            db.BstSessions.Add(s);
            await db.SaveChangesAsync();
            id = s.Id;
        });
        return id;
    }

    public async Task<List<T>> Query<T>(Func<PsyDbContext, IQueryable<T>> query)
        where T : class
    {
        await using var scope = Scope();
        return await query(scope.ServiceProvider.GetRequiredService<PsyDbContext>()).AsNoTracking().ToListAsync();
    }

    public async ValueTask DisposeAsync() => await Services.DisposeAsync();
}

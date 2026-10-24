using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenBP.Data;

/// <summary>Задача, взята з черги <see cref="JobQueue.ClaimAsync"/>.</summary>
/// <param name="Id">Id рядка <c>jobs</c>.</param>
/// <param name="Kind">Вид задачі (<c>jobs.kind</c>).</param>
/// <param name="Payload">JSON корисного навантаження (<c>jobs.payload</c>).</param>
/// <param name="RunAt">Час, не раніше якого задачу можна було взяти.</param>
/// <param name="Attempts">Скільки разів задача вже завершувалась невдало до цього взяття.</param>
/// <param name="WorkerId">Хто тримає lock.</param>
public sealed record ClaimedJob(Guid Id, string Kind, string Payload, DateTimeOffset RunAt, int Attempts, string WorkerId);

/// <summary>Наслідок <see cref="JobQueue.FailAsync"/>.</summary>
public enum JobFailureOutcome
{
    /// <summary>Задача повернута в чергу з новим <c>run_at</c>.</summary>
    Retry,

    /// <summary>Спроби вичерпано (<see cref="JobQueueOptions.MaxAttempts"/>); задачу вилучено з черги.</summary>
    Dropped,

    /// <summary>Lock уже не належить цьому воркеру (протух і повернутий у чергу або задачі немає).</summary>
    LockLost,
}

/// <summary>Результат <see cref="JobQueue.FailAsync"/>.</summary>
/// <param name="Outcome">Що сталося із задачею.</param>
/// <param name="Attempts">Кількість невдалих спроб після цієї; <c>0</c> для <see cref="JobFailureOutcome.LockLost"/>.</param>
/// <param name="NextRunAt">Коли задачу можна взяти знову; лише для <see cref="JobFailureOutcome.Retry"/>.</param>
public sealed record JobFailure(JobFailureOutcome Outcome, int Attempts, DateTimeOffset? NextRunAt);

/// <summary>
/// Черга виконань над таблицею <c>jobs</c> (docs/architecture.md §1, §6): Api ставить задачі, воркери беруть їх
/// через <c>FOR UPDATE SKIP LOCKED</c>, тож кожну задачу одночасно виконує щонайбільше один воркер.
/// <list type="bullet">
/// <item>Взята задача позначається <c>locked_by</c>/<c>locked_at</c>; воркер подовжує lock heartbeat-ом
/// (<see cref="ExtendLockAsync"/>), інакше після <see cref="JobQueueOptions.LockTimeout"/>
/// <see cref="ReleaseStaleLocksAsync"/> повертає її в чергу як невдалу спробу.</item>
/// <item>Успіх (<see cref="CompleteAsync"/>) видаляє рядок; невдача (<see cref="FailAsync"/>) збільшує
/// <c>attempts</c> і відкладає <c>run_at</c> з експоненційним backoff, а після
/// <see cref="JobQueueOptions.MaxAttempts"/> невдач вилучає задачу.</item>
/// <item>Усі операції над узятою задачею перевіряють, що lock ще належить воркеру: після протухання
/// задачу міг узяти інший воркер.</item>
/// <item>Час бере з <paramref name="time"/> (не з годинника Postgres), щоб поведінка була детермінованою в тестах.</item>
/// </list>
/// Потокобезпечний: кожен виклик бере власний <see cref="OpenBpDbContext"/> з фабрики.
/// </summary>
/// <param name="contexts">Фабрика контекстів БД.</param>
/// <param name="options">Налаштування; за замовчуванням <see cref="JobQueueOptions"/>.</param>
/// <param name="time">Годинник; за замовчуванням системний.</param>
public sealed class JobQueue(IDbContextFactory<OpenBpDbContext> contexts, JobQueueOptions? options = null, TimeProvider? time = null)
{
    private readonly IDbContextFactory<OpenBpDbContext> _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    private readonly JobQueueOptions _options = Validated(options ?? new JobQueueOptions());
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Ставить задачу в чергу.</summary>
    /// <param name="kind">Вид задачі, наприклад <c>execution</c>.</param>
    /// <param name="payload">JSON корисного навантаження (без секретів — правило 6 CLAUDE.md).</param>
    /// <param name="runAt">Не раніше якого часу брати задачу; за замовчуванням — одразу.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Id нової задачі (монотонний UUIDv7: за рівного <c>run_at</c> задачі беруться в порядку постановки).</returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> порожній або <paramref name="payload"/> не є JSON.</exception>
    public async Task<Guid> EnqueueAsync(string kind, string payload, DateTimeOffset? runAt = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            JsonDocument.Parse(payload).Dispose();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("payload має бути коректним JSON.", nameof(payload), ex);
        }

        var id = SequentialGuid.Next(_time);
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Jobs.Add(new JobEntity
        {
            Id = id,
            Kind = kind,
            Payload = payload,
            RunAt = (runAt ?? _time.GetUtcNow()).ToUniversalTime(),
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Ключ payload з ідентифікатором групи паралельності (для виконань — id сценарію, F6-06).</summary>
    public const string ConcurrencyKeyField = "concurrencyKey";

    /// <summary>Ключ payload з лімітом одночасно взятих задач групи <see cref="ConcurrencyKeyField"/>.</summary>
    public const string ConcurrencyMaxField = "concurrencyMax";

    /// <summary>
    /// Бере одну готову задачу (<c>run_at</c> не в майбутньому, без lock) одного з видів <paramref name="kinds"/>:
    /// найраніший <c>run_at</c>, далі порядок постановки. Не чекає: немає готових — повертає <c>null</c>.
    /// <para>
    /// Задача з <see cref="ConcurrencyKeyField"/> і <see cref="ConcurrencyMaxField"/> у payload (F6-06) береться, лише
    /// коли задач тієї самої групи з lock'ом менше за ліміт і в групі немає раніших готових задач без lock'а — тож
    /// група з лімітом 1 обробляється строго по черзі в порядку <c>run_at</c>, <c>id</c>. Рішення приймається під
    /// <c>pg_advisory_xact_lock</c> групи, щоб два воркери не взяли понад ліміт одночасно.
    /// </para>
    /// </summary>
    /// <param name="workerId">Ідентифікатор воркера; пишеться в <c>locked_by</c>.</param>
    /// <param name="kinds">Які види задач брати.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ArgumentException"><paramref name="workerId"/> порожній або <paramref name="kinds"/> порожній.</exception>
    public async Task<ClaimedJob?> ClaimAsync(string workerId, IReadOnlyCollection<string> kinds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
        {
            throw new ArgumentException("Потрібен хоча б один вид задач.", nameof(kinds));
        }

        var now = _time.GetUtcNow();
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var connection = await OpenAsync(db, ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        Guid id;
        string? group;
        await using (var select = new NpgsqlCommand(
            $"""
            SELECT j.id, j.payload->>'{ConcurrencyKeyField}'
            FROM jobs j
            WHERE j.locked_by IS NULL AND j.run_at <= @now AND j.kind = ANY(@kinds)
              AND (j.payload->>'{ConcurrencyKeyField}' IS NULL OR j.payload->>'{ConcurrencyMaxField}' IS NULL OR ({GroupHasRoomSql}))
            ORDER BY j.run_at, j.id
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """,
            connection,
            tx))
        {
            select.Parameters.AddWithValue("now", now);
            select.Parameters.AddWithValue("kinds", kinds.ToArray());
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            id = reader.GetGuid(0);
            group = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if (group is not null)
        {
            // Повторна перевірка під замком групи: інший воркер міг узяти задачу групи між знімком вибірки і цим місцем.
            await using (var advisory = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@group, 0))", connection, tx))
            {
                advisory.Parameters.AddWithValue("group", group);
                await advisory.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var recheck = new NpgsqlCommand(
                $"""
                SELECT ({GroupHasRoomSql}) FROM jobs j WHERE j.id = @id
                """,
                connection,
                tx);
            recheck.Parameters.AddWithValue("id", id);
            recheck.Parameters.AddWithValue("now", now);
            if (await recheck.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
            {
                return null;
            }
        }

        ClaimedJob claimed;
        await using (var update = new NpgsqlCommand(
            """
            UPDATE jobs SET locked_by = @worker, locked_at = @now
            WHERE id = @id
            RETURNING id, kind, payload::text, run_at, attempts
            """,
            connection,
            tx))
        {
            update.Parameters.AddWithValue("worker", workerId);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("id", id);
            await using var reader = await update.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            claimed = new ClaimedJob(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetInt32(4),
                workerId);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return claimed;
    }

    /// <summary>
    /// Умова над рядком <c>j</c>: у групі задачі менше взятих, ніж дозволяє її ліміт, і немає раніших готових задач без lock'а.
    /// Параметр <c>@now</c>.
    /// </summary>
    private const string GroupHasRoomSql =
        $"""
        (SELECT count(*) FROM jobs r
         WHERE r.locked_by IS NOT NULL AND r.payload->>'{ConcurrencyKeyField}' = j.payload->>'{ConcurrencyKeyField}')
          < (j.payload->>'{ConcurrencyMaxField}')::int
        AND NOT EXISTS (
          SELECT 1 FROM jobs e
          WHERE e.locked_by IS NULL AND e.run_at <= @now AND e.id <> j.id
            AND e.payload->>'{ConcurrencyKeyField}' = j.payload->>'{ConcurrencyKeyField}'
            AND (e.run_at, e.id) < (j.run_at, j.id))
        """;

    /// <summary>Задача виконана: рядок видаляється.</summary>
    /// <param name="jobId">Id задачі.</param>
    /// <param name="workerId">Воркер, що тримає lock.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>false</c>, якщо lock уже не належить воркеру (задачу повернуто в чергу або її немає).</returns>
    public async Task<bool> CompleteAsync(Guid jobId, string workerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var deleted = await db.Jobs
            .Where(j => j.Id == jobId && j.LockedBy == workerId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return deleted == 1;
    }

    /// <summary>
    /// Задача не виконана: <c>attempts</c> + 1, lock знімається, <c>run_at</c> = зараз + backoff
    /// (<see cref="JobQueueOptions.RetryDelay"/>). Після <see cref="JobQueueOptions.MaxAttempts"/> невдач задача вилучається.
    /// </summary>
    /// <param name="jobId">Id задачі.</param>
    /// <param name="workerId">Воркер, що тримає lock.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<JobFailure> FailAsync(Guid jobId, string workerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var connection = await OpenAsync(db, ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        int attempts;
        await using (var select = new NpgsqlCommand("SELECT attempts FROM jobs WHERE id = @id AND locked_by = @worker FOR UPDATE", connection, tx))
        {
            select.Parameters.AddWithValue("id", jobId);
            select.Parameters.AddWithValue("worker", workerId);
            var current = await select.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (current is not int held)
            {
                return new JobFailure(JobFailureOutcome.LockLost, 0, null);
            }

            attempts = held + 1;
        }

        if (attempts >= _options.MaxAttempts)
        {
            await using var delete = new NpgsqlCommand("DELETE FROM jobs WHERE id = @id", connection, tx);
            delete.Parameters.AddWithValue("id", jobId);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new JobFailure(JobFailureOutcome.Dropped, attempts, null);
        }

        var nextRunAt = _time.GetUtcNow() + _options.RetryDelay(attempts);
        await using (var update = new NpgsqlCommand(
            "UPDATE jobs SET attempts = @attempts, run_at = @run_at, locked_by = NULL, locked_at = NULL WHERE id = @id",
            connection,
            tx))
        {
            update.Parameters.AddWithValue("attempts", attempts);
            update.Parameters.AddWithValue("run_at", nextRunAt);
            update.Parameters.AddWithValue("id", jobId);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new JobFailure(JobFailureOutcome.Retry, attempts, nextRunAt);
    }

    /// <summary>Heartbeat: оновлює <c>locked_at</c>, щоб lock не протух.</summary>
    /// <param name="jobId">Id задачі.</param>
    /// <param name="workerId">Воркер, що тримає lock.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>false</c>, якщо lock уже не належить воркеру — виконання варто зупинити.</returns>
    public async Task<bool> ExtendLockAsync(Guid jobId, string workerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        var now = _time.GetUtcNow();
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var updated = await db.Jobs
            .Where(j => j.Id == jobId && j.LockedBy == workerId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LockedAt, now), ct)
            .ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>
    /// Повертає в чергу задачі, чий lock не подовжували довше за <see cref="JobQueueOptions.LockTimeout"/>
    /// (воркер упав або завис). Протухання рахується невдалою спробою: <c>attempts</c> + 1, <c>run_at</c> без змін;
    /// задачі, що вичерпали <see cref="JobQueueOptions.MaxAttempts"/>, вилучаються.
    /// </summary>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки задач повернуто в чергу (без вилучених).</returns>
    public async Task<int> ReleaseStaleLocksAsync(CancellationToken ct = default)
    {
        var threshold = _time.GetUtcNow() - _options.LockTimeout;
        var max = _options.MaxAttempts;
        await using var db = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Jobs
            .Where(j => j.LockedBy != null && j.LockedAt < threshold && j.Attempts + 1 >= max)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        var released = await db.Jobs
            .Where(j => j.LockedBy != null && j.LockedAt < threshold)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                    .SetProperty(j => j.LockedBy, (string?)null)
                    .SetProperty(j => j.LockedAt, (DateTimeOffset?)null),
                ct)
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return released;
    }

    private static JobQueueOptions Validated(JobQueueOptions options)
    {
        options.Validate();
        return options;
    }

    private static async Task<NpgsqlConnection> OpenAsync(OpenBpDbContext db, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        return connection;
    }
}

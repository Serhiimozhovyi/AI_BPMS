using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace OpenBP.Data.Tests;

/// <summary><see cref="JobQueue"/> над таблицею <c>jobs</c>: SKIP LOCKED, backoff, протухлі lock.</summary>
[Collection(PostgresCollection.Name)]
public class JobQueueTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    // Кожен тест працює зі своїм kind: БД спільна для колекції.
    private readonly string _kind = "test-" + Guid.NewGuid().ToString("N");

    private async Task<(JobQueue Queue, ManualClock Clock, string Cs)> QueueAsync(JobQueueOptions? options = null, bool isolated = false)
    {
        // ReleaseStaleLocksAsync працює з усією таблицею, тому тести на нього беруть окрему БД.
        string cs;
        if (isolated)
        {
            cs = await postgres.NewDatabaseAsync();
            await using var db = PostgresFixture.CreateContext(cs);
            await db.Database.MigrateAsync();
        }
        else
        {
            cs = await postgres.MigratedDatabaseAsync();
        }

        var clock = new ManualClock(T0);
        var queue = new JobQueue(new PooledDbContextFactory<OpenBpDbContext>(PostgresFixture.Options(cs)), options, clock);
        return (queue, clock, cs);
    }

    private static async Task<JobEntity?> RowAsync(string cs, Guid id)
    {
        await using var db = PostgresFixture.CreateContext(cs);
        return await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id);
    }

    [Fact]
    public async Task Enqueue_ThenClaim_ReturnsJobAndMarksLock()
    {
        var (queue, _, cs) = await QueueAsync();

        var id = await queue.EnqueueAsync(_kind, """{"executionId":"e1"}""");
        var job = await queue.ClaimAsync("w1", [_kind]);

        Assert.NotNull(job);
        Assert.Equal(id, job.Id);
        Assert.Equal(_kind, job.Kind);
        Assert.Equal("e1", System.Text.Json.JsonDocument.Parse(job.Payload).RootElement.GetProperty("executionId").GetString());
        Assert.Equal(T0, job.RunAt);
        Assert.Equal(0, job.Attempts);
        Assert.Equal("w1", job.WorkerId);

        var row = await RowAsync(cs, id);
        Assert.NotNull(row);
        Assert.Equal("w1", row.LockedBy);
        Assert.Equal(T0, row.LockedAt);

        Assert.Null(await queue.ClaimAsync("w2", [_kind]));
    }

    [Fact]
    public async Task Enqueue_RejectsInvalidJsonAndEmptyKind()
    {
        var (queue, _, _) = await QueueAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(_kind, "{не json"));
        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(" ", "{}"));
    }

    [Fact]
    public async Task Claim_SkipsFutureRunAt_UntilTimeComes()
    {
        var (queue, clock, _) = await QueueAsync();

        var id = await queue.EnqueueAsync(_kind, "{}", T0 + TimeSpan.FromMinutes(5));

        Assert.Null(await queue.ClaimAsync("w1", [_kind]));
        clock.Now = T0 + TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1);
        Assert.Null(await queue.ClaimAsync("w1", [_kind]));

        clock.Now = T0 + TimeSpan.FromMinutes(5);
        var job = await queue.ClaimAsync("w1", [_kind]);
        Assert.Equal(id, job?.Id);
    }

    [Fact]
    public async Task Claim_TakesOnlyRequestedKinds()
    {
        var (queue, _, _) = await QueueAsync();
        var other = _kind + "-other";

        var id = await queue.EnqueueAsync(_kind, "{}");
        var otherId = await queue.EnqueueAsync(other, "{}");

        Assert.Null(await queue.ClaimAsync("w1", [_kind + "-none"]));
        Assert.Equal(otherId, (await queue.ClaimAsync("w1", [other]))?.Id);
        Assert.Equal(id, (await queue.ClaimAsync("w1", [_kind, other]))?.Id);
    }

    [Fact]
    public async Task Claim_OrdersByRunAt_ThenByEnqueueOrder()
    {
        var (queue, clock, _) = await QueueAsync();

        var later = await queue.EnqueueAsync(_kind, "{}", T0 + TimeSpan.FromSeconds(30));
        var first = await queue.EnqueueAsync(_kind, "{}");
        var second = await queue.EnqueueAsync(_kind, "{}");
        clock.Now = T0 + TimeSpan.FromMinutes(1);

        Assert.Equal(first, (await queue.ClaimAsync("w1", [_kind]))?.Id);
        Assert.Equal(second, (await queue.ClaimAsync("w1", [_kind]))?.Id);
        Assert.Equal(later, (await queue.ClaimAsync("w1", [_kind]))?.Id);
    }

    [Fact]
    public async Task Claim_ValidatesArguments()
    {
        var (queue, _, _) = await QueueAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => queue.ClaimAsync("", [_kind]));
        await Assert.ThrowsAsync<ArgumentException>(() => queue.ClaimAsync("w1", []));
    }

    [Fact]
    public async Task EightConsumers_200Jobs_EachClaimedExactlyOnce()
    {
        var (queue, _, _) = await QueueAsync();
        const int jobs = 200;
        const int consumers = 8;

        var expected = new HashSet<Guid>();
        for (var i = 0; i < jobs; i++)
        {
            expected.Add(await queue.EnqueueAsync(_kind, $$"""{"n":{{i}}}"""));
        }

        var claimed = new ConcurrentBag<(string Worker, Guid Id)>();
        await Task.WhenAll(Enumerable.Range(0, consumers).Select(async n =>
        {
            var worker = $"w{n}";
            while (await queue.ClaimAsync(worker, [_kind]) is { } job)
            {
                claimed.Add((worker, job.Id));
                Assert.True(await queue.CompleteAsync(job.Id, worker));
            }
        }));

        Assert.Equal(jobs, claimed.Count);
        Assert.Equal(expected, claimed.Select(c => c.Id).ToHashSet());
        Assert.Null(await queue.ClaimAsync("w-late", [_kind]));
    }

    [Fact]
    public async Task Complete_DeletesJob_OnlyForLockOwner()
    {
        var (queue, _, cs) = await QueueAsync();
        var id = await queue.EnqueueAsync(_kind, "{}");
        var job = await queue.ClaimAsync("w1", [_kind]);
        Assert.NotNull(job);

        Assert.False(await queue.CompleteAsync(job.Id, "w2"));
        Assert.NotNull(await RowAsync(cs, id));

        Assert.True(await queue.CompleteAsync(job.Id, "w1"));
        Assert.Null(await RowAsync(cs, id));
        Assert.False(await queue.CompleteAsync(job.Id, "w1"));
    }

    [Fact]
    public async Task Fail_RequeuesWithBackoff_AndCountsAttempts()
    {
        var options = new JobQueueOptions { RetryBaseDelay = TimeSpan.FromSeconds(10), RetryMaxDelay = TimeSpan.FromSeconds(15), MaxAttempts = 5 };
        var (queue, clock, cs) = await QueueAsync(options);
        var id = await queue.EnqueueAsync(_kind, "{}");
        var job = await queue.ClaimAsync("w1", [_kind]);
        Assert.NotNull(job);

        var failure = await queue.FailAsync(job.Id, "w1");
        Assert.Equal(new JobFailure(JobFailureOutcome.Retry, 1, T0 + TimeSpan.FromSeconds(10)), failure);
        var row = await RowAsync(cs, id);
        Assert.NotNull(row);
        Assert.Null(row.LockedBy);
        Assert.Null(row.LockedAt);
        Assert.Equal(1, row.Attempts);

        // До run_at задачу не брати; після — беруть із лічильником спроб.
        Assert.Null(await queue.ClaimAsync("w2", [_kind]));
        clock.Now = T0 + TimeSpan.FromSeconds(10);
        var retried = await queue.ClaimAsync("w2", [_kind]);
        Assert.NotNull(retried);
        Assert.Equal(id, retried.Id);
        Assert.Equal(1, retried.Attempts);

        // Друга невдача: удвічі довше, але не більше RetryMaxDelay.
        failure = await queue.FailAsync(id, "w2");
        Assert.Equal(new JobFailure(JobFailureOutcome.Retry, 2, clock.Now + TimeSpan.FromSeconds(15)), failure);
    }

    [Fact]
    public async Task Fail_AfterMaxAttempts_DropsJob()
    {
        var (queue, clock, cs) = await QueueAsync(new JobQueueOptions { MaxAttempts = 2, RetryBaseDelay = TimeSpan.Zero });
        var id = await queue.EnqueueAsync(_kind, "{}");

        Assert.NotNull(await queue.ClaimAsync("w1", [_kind]));
        Assert.Equal(JobFailureOutcome.Retry, (await queue.FailAsync(id, "w1")).Outcome);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.NotNull(await queue.ClaimAsync("w1", [_kind]));

        var failure = await queue.FailAsync(id, "w1");
        Assert.Equal(new JobFailure(JobFailureOutcome.Dropped, 2, null), failure);
        Assert.Null(await RowAsync(cs, id));
        Assert.Null(await queue.ClaimAsync("w1", [_kind]));
    }

    [Fact]
    public async Task Fail_WithoutLock_ReportsLockLost()
    {
        var (queue, _, cs) = await QueueAsync();
        var id = await queue.EnqueueAsync(_kind, "{}");
        Assert.NotNull(await queue.ClaimAsync("w1", [_kind]));

        var failure = await queue.FailAsync(id, "w2");
        Assert.Equal(new JobFailure(JobFailureOutcome.LockLost, 0, null), failure);
        var row = await RowAsync(cs, id);
        Assert.Equal("w1", row?.LockedBy);
        Assert.Equal(0, row?.Attempts);

        Assert.Equal(JobFailureOutcome.LockLost, (await queue.FailAsync(Guid.NewGuid(), "w1")).Outcome);
    }

    [Fact]
    public async Task ExtendLock_UpdatesLockedAt_OnlyForLockOwner()
    {
        var (queue, clock, cs) = await QueueAsync();
        var id = await queue.EnqueueAsync(_kind, "{}");
        Assert.NotNull(await queue.ClaimAsync("w1", [_kind]));

        clock.Now = T0 + TimeSpan.FromMinutes(2);
        Assert.False(await queue.ExtendLockAsync(id, "w2"));
        Assert.Equal(T0, (await RowAsync(cs, id))?.LockedAt);

        Assert.True(await queue.ExtendLockAsync(id, "w1"));
        Assert.Equal(clock.Now, (await RowAsync(cs, id))?.LockedAt);
    }

    [Fact]
    public async Task ReleaseStaleLocks_ReturnsJobToQueue_AsFailedAttempt()
    {
        var (queue, clock, cs) = await QueueAsync(new JobQueueOptions { LockTimeout = TimeSpan.FromMinutes(5) }, isolated: true);
        var id = await queue.EnqueueAsync(_kind, "{}");
        Assert.NotNull(await queue.ClaimAsync("w1", [_kind]));

        clock.Now = T0 + TimeSpan.FromMinutes(5);
        Assert.Equal(0, await queue.ReleaseStaleLocksAsync());
        Assert.Null(await queue.ClaimAsync("w2", [_kind]));

        clock.Now = T0 + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        Assert.Equal(1, await queue.ReleaseStaleLocksAsync());
        var row = await RowAsync(cs, id);
        Assert.NotNull(row);
        Assert.Null(row.LockedBy);
        Assert.Null(row.LockedAt);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(T0, row.RunAt);

        var reclaimed = await queue.ClaimAsync("w2", [_kind]);
        Assert.Equal(id, reclaimed?.Id);
        Assert.Equal(1, reclaimed?.Attempts);

        // Старий воркер уже не володіє задачею.
        Assert.False(await queue.ExtendLockAsync(id, "w1"));
        Assert.False(await queue.CompleteAsync(id, "w1"));
        Assert.True(await queue.CompleteAsync(id, "w2"));
    }

    [Fact]
    public async Task ReleaseStaleLocks_KeepsHeartbeatedLock()
    {
        var (queue, clock, cs) = await QueueAsync(new JobQueueOptions { LockTimeout = TimeSpan.FromMinutes(5) }, isolated: true);
        var id = await queue.EnqueueAsync(_kind, "{}");
        Assert.NotNull(await queue.ClaimAsync("w1", [_kind]));

        clock.Now = T0 + TimeSpan.FromMinutes(4);
        Assert.True(await queue.ExtendLockAsync(id, "w1"));
        clock.Now = T0 + TimeSpan.FromMinutes(8);

        Assert.Equal(0, await queue.ReleaseStaleLocksAsync());
        Assert.Equal("w1", (await RowAsync(cs, id))?.LockedBy);
        Assert.Null(await queue.ClaimAsync("w2", [_kind]));
    }

    [Fact]
    public async Task ReleaseStaleLocks_DropsJobThatExhaustedAttempts()
    {
        var (queue, clock, cs) = await QueueAsync(new JobQueueOptions { LockTimeout = TimeSpan.FromMinutes(1), MaxAttempts = 1 }, isolated: true);
        var id = await queue.EnqueueAsync(_kind, "{}");
        Assert.NotNull(await queue.ClaimAsync("w1", [_kind]));

        clock.Now = T0 + TimeSpan.FromMinutes(2);
        Assert.Equal(0, await queue.ReleaseStaleLocksAsync());
        Assert.Null(await RowAsync(cs, id));
    }

    [Fact]
    public void Options_RetryDelay_DoublesUpToMax()
    {
        var options = new JobQueueOptions { RetryBaseDelay = TimeSpan.FromSeconds(10), RetryMaxDelay = TimeSpan.FromSeconds(45) };

        Assert.Equal(TimeSpan.FromSeconds(10), options.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(20), options.RetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(40), options.RetryDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(45), options.RetryDelay(4));
        Assert.Equal(TimeSpan.FromSeconds(45), options.RetryDelay(100));
    }

    [Fact]
    public void Options_Validate_RejectsInconsistentValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobQueueOptions { MaxAttempts = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobQueueOptions { LockTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JobQueueOptions { RetryBaseDelay = TimeSpan.FromMinutes(20), RetryMaxDelay = TimeSpan.FromMinutes(10) }.Validate());
        new JobQueueOptions().Validate();
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

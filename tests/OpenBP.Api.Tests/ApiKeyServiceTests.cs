using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary><c>last_used_at</c> оновлюється не частіше раз на хвилину (docs/architecture.md §10.6).</summary>
[Collection(ApiCollection.Name)]
public class ApiKeyServiceTests(ApiFactory api)
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => id;

        public string? Email => "u@openbp.test";

        public UserRole? Role => UserRole.Viewer;
    }

    [Fact]
    public async Task Last_used_at_is_updated_at_most_once_per_minute()
    {
        var user = await api.CreateUserAsync(UserRole.Viewer);
        var t0 = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTime(t0);
        await using var db = api.CreateDbContext();
        var service = new ApiKeyService(db, new FixedUser(user.Id), time);
        var key = await service.CreateAsync("k", CancellationToken.None);

        async Task<DateTimeOffset?> UseAt(DateTimeOffset at)
        {
            time.Now = at;
            Assert.NotNull(await service.AuthenticateAsync(key.Key, CancellationToken.None));
            await using var read = api.CreateDbContext();
            return read.ApiKeys.Single(k => k.Id == key.Id).LastUsedAt;
        }

        Assert.Equal(t0, await UseAt(t0));
        Assert.Equal(t0, await UseAt(t0.AddSeconds(59)));
        Assert.Equal(t0.AddSeconds(61), await UseAt(t0.AddSeconds(61)));
    }
}

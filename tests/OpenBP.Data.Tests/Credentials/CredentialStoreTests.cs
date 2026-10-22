using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenBP.Data.Credentials;
using OpenBP.Engine;

namespace OpenBP.Data.Tests.Credentials;

/// <summary><see cref="CredentialStore"/> і <see cref="PostgresCredentialProvider"/> на postgres:16.</summary>
[Collection(PostgresCollection.Name)]
public class CredentialStoreTests(PostgresFixture postgres)
{
    private const string Secret = "sk-test-very-secret-value";

    private static readonly JsonObject Data = new()
    {
        ["apiKey"] = Secret,
        ["headers"] = new JsonObject { ["X-Token"] = "Токен-42" },
    };

    private static CredentialEncryptionKey NewKey() =>
        CredentialEncryptionKey.FromBase64(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    private async Task<(CredentialStore Store, string Cs, IDbContextFactory<OpenBpDbContext> Contexts)> StoreAsync(CredentialEncryptionKey? key = null)
    {
        var cs = await postgres.MigratedDatabaseAsync();
        var contexts = new PooledDbContextFactory<OpenBpDbContext>(PostgresFixture.Options(cs));
        return (new CredentialStore(contexts, key ?? NewKey()), cs, contexts);
    }

    [Fact]
    public async Task RoundTrip_ReturnsSameData()
    {
        var (store, _, _) = await StoreAsync();
        var creator = Guid.NewGuid();

        var id = await store.CreateAsync("Anthropic", "ai.anthropic", Data, creator, CancellationToken.None);
        var data = await store.GetDataAsync(id, CancellationToken.None);

        Assert.True(JsonNode.DeepEquals(Data, data));
        Assert.NotSame(await store.GetDataAsync(id, CancellationToken.None), data);
    }

    [Fact]
    public async Task Database_HasNoPlaintext()
    {
        var (store, cs, _) = await StoreAsync();

        var id = await store.CreateAsync("c", "t", Data, null, CancellationToken.None);

        await using var db = PostgresFixture.CreateContext(cs);
        var row = await db.Credentials.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(CredentialStore.NonceSize, row.Nonce.Length);
        Assert.Equal(Encoding.UTF8.GetByteCount(Data.ToJsonString()) + CredentialStore.TagSize, row.DataEncrypted.Length);
        Assert.Equal(-1, row.DataEncrypted.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Secret)));
        Assert.Equal(-1, row.DataEncrypted.AsSpan().IndexOf(Encoding.UTF8.GetBytes("apiKey")));
        var asText = await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM credentials WHERE id = {id} AND (encode(data_encrypted, 'escape') LIKE {"%" + Secret + "%"} OR name LIKE {"%" + Secret + "%"} OR type LIKE {"%" + Secret + "%"})")
            .SingleAsync();
        Assert.Equal(0, asText);
    }

    [Fact]
    public async Task SameData_GetsNewNonceEachWrite()
    {
        var (store, cs, _) = await StoreAsync();

        var a = await store.CreateAsync("a", "t", Data, null, CancellationToken.None);
        var b = await store.CreateAsync("b", "t", Data, null, CancellationToken.None);
        await using var db = PostgresFixture.CreateContext(cs);
        var before = await db.Credentials.AsNoTracking().SingleAsync(c => c.Id == a);
        Assert.True(await store.UpdateDataAsync(a, Data, CancellationToken.None));
        var after = await db.Credentials.AsNoTracking().SingleAsync(c => c.Id == a);
        var other = await db.Credentials.AsNoTracking().SingleAsync(c => c.Id == b);

        Assert.NotEqual(before.Nonce, after.Nonce);
        Assert.NotEqual(before.DataEncrypted, after.DataEncrypted);
        Assert.NotEqual(before.Nonce, other.Nonce);
    }

    [Fact]
    public async Task UpdateDataAsync_ReplacesData()
    {
        var (store, _, _) = await StoreAsync();
        var id = await store.CreateAsync("c", "t", Data, null, CancellationToken.None);
        var updated = new JsonObject { ["apiKey"] = "new-value" };

        Assert.True(await store.UpdateDataAsync(id, updated, CancellationToken.None));
        Assert.False(await store.UpdateDataAsync(Guid.NewGuid(), updated, CancellationToken.None));

        Assert.True(JsonNode.DeepEquals(updated, await store.GetDataAsync(id, CancellationToken.None)));
    }

    [Fact]
    public async Task GetDataAsync_Unknown_ReturnsNull()
    {
        var (store, _, _) = await StoreAsync();

        Assert.Null(await store.GetDataAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task OtherKey_FailsToDecrypt()
    {
        var (store, _, contexts) = await StoreAsync();
        var id = await store.CreateAsync("c", "t", Data, null, CancellationToken.None);
        var otherKeyStore = new CredentialStore(contexts, NewKey());

        var ex = await Assert.ThrowsAsync<CredentialDecryptionException>(() => otherKeyStore.GetDataAsync(id, CancellationToken.None));

        Assert.Equal(id, ex.CredentialId);
        Assert.DoesNotContain(Secret, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("nonce-length")]
    [InlineData("cipher")]
    [InlineData("tag")]
    public async Task CorruptedRow_FailsToDecrypt(string corruption)
    {
        var (store, cs, _) = await StoreAsync();
        var id = await store.CreateAsync("c", "t", Data, null, CancellationToken.None);
        await using (var db = PostgresFixture.CreateContext(cs))
        {
            var row = await db.Credentials.SingleAsync(c => c.Id == id);
            switch (corruption)
            {
                case "nonce":
                    row.Nonce = [.. row.Nonce];
                    row.Nonce[0] ^= 0xFF;
                    break;
                case "nonce-length":
                    row.Nonce = row.Nonce[..8];
                    break;
                case "cipher":
                    row.DataEncrypted = [.. row.DataEncrypted];
                    row.DataEncrypted[0] ^= 0x01;
                    break;
                default:
                    row.DataEncrypted = [.. row.DataEncrypted];
                    row.DataEncrypted[^1] ^= 0x01;
                    break;
            }

            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<CredentialDecryptionException>(() => store.GetDataAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task DataMovedToAnotherRow_FailsToDecrypt()
    {
        var (store, cs, _) = await StoreAsync();
        var source = await store.CreateAsync("a", "t", Data, null, CancellationToken.None);
        var target = await store.CreateAsync("b", "t", new JsonObject { ["apiKey"] = "інше" }, null, CancellationToken.None);
        await using (var db = PostgresFixture.CreateContext(cs))
        {
            var from = await db.Credentials.SingleAsync(c => c.Id == source);
            var to = await db.Credentials.SingleAsync(c => c.Id == target);
            to.DataEncrypted = from.DataEncrypted;
            to.Nonce = from.Nonce;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<CredentialDecryptionException>(() => store.GetDataAsync(target, CancellationToken.None));
    }

    [Fact]
    public async Task Provider_ReturnsDataAndRegistersSecrets()
    {
        var (store, _, _) = await StoreAsync();
        var id = await store.CreateAsync("c", "t", Data, null, CancellationToken.None);
        var scrubber = new SecretScrubber();
        var provider = new PostgresCredentialProvider(store, scrubber);

        var data = await provider.GetAsync(id.ToString(), CancellationToken.None);

        Assert.True(JsonNode.DeepEquals(Data, data));
        Assert.Equal("key=***, token=***", scrubber.Scrub($"key={Secret}, token=Токен-42"));
    }

    [Theory]
    [InlineData("cred_1")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public async Task Provider_Unknown_ThrowsCredentialNotFound(string credentialId)
    {
        var (store, _, _) = await StoreAsync();
        var provider = new PostgresCredentialProvider(store);

        var ex = await Assert.ThrowsAsync<CredentialNotFoundException>(() => provider.GetAsync(credentialId, CancellationToken.None));

        Assert.Equal(credentialId, ex.CredentialId);
    }
}

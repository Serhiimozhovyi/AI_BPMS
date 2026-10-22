using System.Text;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Testing.Tests;

public class InMemoryBinaryStoreTests
{
    [Fact]
    public async Task PutAsync_FillsReference_AndOpenReadReturnsSameBytes()
    {
        var store = new InMemoryBinaryStore();
        var bytes = Encoding.UTF8.GetBytes("привіт");
        using var input = new MemoryStream(bytes);

        var reference = await store.PutAsync(input, "text/plain", "hello.txt", CancellationToken.None);

        Assert.NotEmpty(reference.Id);
        Assert.Equal("text/plain", reference.MimeType);
        Assert.Equal("hello.txt", reference.FileName);
        Assert.Equal("txt", reference.FileExtension);
        Assert.Equal(bytes.Length, reference.FileSize);
        Assert.Equal(InMemoryBinaryStore.StorageName, reference.Storage);
        Assert.Equal(64, reference.Sha256!.Length);
        Assert.Equal(reference.Sha256, reference.Sha256.ToLowerInvariant());
        Assert.True(store.Contains(reference.Id));
        Assert.Equal(1, store.Count);

        await using var output = await store.OpenReadAsync(reference, CancellationToken.None);
        using var buffer = new MemoryStream();
        await output.CopyToAsync(buffer);
        Assert.Equal(bytes, buffer.ToArray());
    }

    [Fact]
    public async Task PutAsync_WithoutFileName_LeavesNameAndExtensionNull()
    {
        var store = new InMemoryBinaryStore();
        using var input = new MemoryStream([1, 2, 3]);

        var reference = await store.PutAsync(input, "application/octet-stream", null, CancellationToken.None);

        Assert.Null(reference.FileName);
        Assert.Null(reference.FileExtension);
        Assert.Equal(3, reference.FileSize);
    }

    [Fact]
    public async Task OpenReadAsync_Throws_ForUnknownId()
    {
        var store = new InMemoryBinaryStore();

        var ex = await Assert.ThrowsAsync<BinaryNotFoundException>(() =>
            store.OpenReadAsync(new BinaryRef { Id = "nope" }, CancellationToken.None));

        Assert.Equal("nope", ex.Id);
    }
}

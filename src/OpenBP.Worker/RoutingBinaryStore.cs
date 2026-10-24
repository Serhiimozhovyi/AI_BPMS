using OpenBP.Engine;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker;

/// <summary>
/// Пише в одне сховище, а читає зі сховища за <see cref="BinaryRef.Storage"/> (F6-18): після переходу з локальної папки на
/// S3 старі посилання <c>local</c> лишаються читабельними. Посилання без <c>storage</c> — старі локальні файли.
/// </summary>
internal sealed class RoutingBinaryStore(IBinaryStore writer, IReadOnlyDictionary<string, IBinaryStore> readers) : IBinaryStore
{
    /// <inheritdoc />
    public Task<BinaryRef> PutAsync(Stream content, string mimeType, string? fileName, CancellationToken ct) =>
        writer.PutAsync(content, mimeType, fileName, ct);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(BinaryRef reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return readers.TryGetValue(reference.Storage ?? LocalBinaryStore.StorageName, out var store)
            ? store.OpenReadAsync(reference, ct)
            : throw new BinaryNotFoundException(reference.Id);
    }
}

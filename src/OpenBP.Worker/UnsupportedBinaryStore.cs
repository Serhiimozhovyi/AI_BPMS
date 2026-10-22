using OpenBP.Engine;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker;

/// <summary>
/// Заглушка <see cref="IBinaryStore"/>: файлового сховища (локальна папка / S3, docs/architecture.md §6) ще немає,
/// тож вузол, що зберігає або читає бінарні дані, завершується зрозумілою помилкою, а не втрачає файл.
/// </summary>
internal sealed class UnsupportedBinaryStore : IBinaryStore
{
    private const string Message = "Файлове сховище бінарних даних у воркері ще не налаштоване.";

    /// <inheritdoc />
    public Task<BinaryRef> PutAsync(Stream content, string mimeType, string? fileName, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(BinaryRef reference, CancellationToken ct) =>
        throw new NotSupportedException(Message);
}

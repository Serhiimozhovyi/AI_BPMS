using OpenBP.Engine;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker;

/// <summary>
/// Заглушка <see cref="IBinaryStore"/>, коли файлове сховище не налаштоване (<c>Storage:LocalPath</c> не задано):
/// вузол, що зберігає або читає бінарні дані, завершується зрозумілою помилкою, а не втрачає файл.
/// </summary>
internal sealed class UnsupportedBinaryStore : IBinaryStore
{
    private const string Message = "Файлове сховище бінарних даних у воркері не налаштоване: задайте Storage:LocalPath (змінна Storage__LocalPath).";

    /// <inheritdoc />
    public Task<BinaryRef> PutAsync(Stream content, string mimeType, string? fileName, CancellationToken ct) =>
        throw new NotSupportedException(Message);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(BinaryRef reference, CancellationToken ct) =>
        throw new NotSupportedException(Message);
}

using System.Security.Cryptography;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Testing;

/// <summary>
/// <see cref="IBinaryStore"/> у пам'яті: байти тримаються у словнику, <see cref="BinaryRef.Storage"/> — <c>memory</c>.
/// Потокобезпечний.
/// </summary>
public sealed class InMemoryBinaryStore : IBinaryStore
{
    /// <summary>Значення <see cref="BinaryRef.Storage"/> для файлів цього сховища.</summary>
    public const string StorageName = "memory";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (BinaryRef Ref, byte[] Bytes)> _files = new(StringComparer.Ordinal);

    /// <summary>Кількість збережених файлів.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _files.Count;
            }
        }
    }

    /// <inheritdoc />
    public async Task<BinaryRef> PutAsync(Stream content, string mimeType, string? fileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(mimeType);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        var reference = new BinaryRef
        {
            Id = Guid.NewGuid().ToString("N"),
            MimeType = mimeType,
            FileName = fileName,
            FileExtension = ExtensionOf(fileName),
            FileSize = bytes.LongLength,
            Storage = StorageName,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };

        lock (_lock)
        {
            _files[reference.Id] = (reference, bytes);
        }

        return reference;
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(BinaryRef reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_files.TryGetValue(reference.Id, out var file))
            {
                throw new BinaryNotFoundException(reference.Id);
            }

            return Task.FromResult<Stream>(new MemoryStream(file.Bytes, writable: false));
        }
    }

    /// <summary>Чи є файл з таким ключем.</summary>
    /// <param name="id">Ключ файлу.</param>
    public bool Contains(string id)
    {
        lock (_lock)
        {
            return _files.ContainsKey(id);
        }
    }

    private static string? ExtensionOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var extension = Path.GetExtension(fileName);
        return extension.Length > 1 ? extension[1..] : null;
    }
}

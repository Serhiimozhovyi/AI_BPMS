using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OpenBP.Engine;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker;

/// <summary>
/// <see cref="IBinaryStore"/> у локальній папці (docs/architecture.md §6): файл лежить у <c>&lt;корінь&gt;/&lt;рік&gt;/&lt;місяць&gt;/&lt;id&gt;</c>,
/// <see cref="BinaryRef.Storage"/> — <c>local</c>, <see cref="BinaryRef.Directory"/> — <c>рік/місяць</c>. <c>BinaryRef</c> може
/// прийти з даних користувача (<c>pinData</c>, <c>startItems</c>), тож читання приймає лише ключі й каталоги, які створює
/// саме сховище, і не виходить за корінь.
/// </summary>
public sealed partial class LocalBinaryStore : IBinaryStore
{
    /// <summary>Значення <see cref="BinaryRef.Storage"/> для файлів цього сховища.</summary>
    public const string StorageName = "local";

    private readonly string _root;
    private readonly TimeProvider _time;

    /// <summary>Створює сховище; папка створюється, якщо її немає.</summary>
    /// <param name="root">Коренева папка.</param>
    /// <param name="time">Годинник для каталогу рік/місяць; <c>null</c> — <see cref="TimeProvider.System"/>.</param>
    public LocalBinaryStore(string root, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc />
    public async Task<BinaryRef> PutAsync(Stream content, string mimeType, string? fileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(mimeType);

        var id = Guid.NewGuid().ToString("N");
        var directory = _time.GetUtcNow().ToString("yyyy'/'MM", CultureInfo.InvariantCulture);
        var folder = Path.Combine(_root, directory);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id);
        var temp = path + ".tmp";

        long size;
        byte[] hash;
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                size = file.Length;
            }

            hash = sha.GetHashAndReset();

            // Файл з'являється під своїм ключем лише повністю записаним.
            File.Move(temp, path);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }

        return new BinaryRef
        {
            Id = id,
            MimeType = mimeType,
            FileName = fileName,
            FileExtension = Path.GetExtension(fileName) is { Length: > 1 } extension ? extension[1..].ToLowerInvariant() : null,
            FileSize = size,
            Storage = StorageName,
            Directory = directory,
            Sha256 = Convert.ToHexStringLower(hash),
        };
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(BinaryRef reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ct.ThrowIfCancellationRequested();
        if (reference.Storage is not (null or StorageName) || !FileId().IsMatch(reference.Id) ||
            !(reference.Directory is null || MonthDirectory().IsMatch(reference.Directory)))
        {
            throw new BinaryNotFoundException(reference.Id);
        }

        var path = Path.Combine(_root, reference.Directory ?? "", reference.Id);
        try
        {
            return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new BinaryNotFoundException(reference.Id);
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex FileId();

    [GeneratedRegex("^[0-9]{4}/[0-9]{2}$")]
    private static partial Regex MonthDirectory();
}

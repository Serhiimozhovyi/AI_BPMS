using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenBP.Engine;
using OpenBP.Shared.Workflow;

namespace OpenBP.Data;

/// <summary>
/// <see cref="IBinaryStore"/> у таблиці <c>binary_files</c> (F6-03): файли, які приймає Api (поля-файли форм), бо файлове
/// сховище (<c>Storage:LocalPath</c>, S3) налаштоване лише у воркера. <see cref="BinaryRef.Storage"/> — <see cref="StorageName"/>;
/// воркер читає ці посилання через свій маршрутизатор сховищ. Файл прив'язується до виконання
/// (<see cref="AttachAsync"/>) і видаляється разом із ним; розміри обмежує той, хто пише (ліміти форм).
/// </summary>
/// <param name="db">Фабрика контекстів БД.</param>
/// <param name="time">Годинник для <c>created_at</c>; <c>null</c> — системний.</param>
public sealed class PostgresBinaryStore(IDbContextFactory<OpenBpDbContext> db, TimeProvider? time = null) : IBinaryStore
{
    /// <summary>Значення <see cref="BinaryRef.Storage"/> для файлів цього сховища.</summary>
    public const string StorageName = "postgres";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<BinaryRef> PutAsync(Stream content, string mimeType, string? fileName, CancellationToken ct) =>
        PutAsync(content, mimeType, fileName, null, ct);

    /// <summary>Зберігає файл і одразу прив'язує його до виконання.</summary>
    /// <param name="content">Вміст; читається до кінця, не закривається.</param>
    /// <param name="mimeType">MIME-тип.</param>
    /// <param name="fileName">Оригінальна назва файлу.</param>
    /// <param name="executionId">Виконання; <c>null</c> — прив'язати пізніше через <see cref="AttachAsync"/>.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<BinaryRef> PutAsync(Stream content, string mimeType, string? fileName, Guid? executionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(mimeType);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        var entity = new BinaryFileEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            ExecutionId = executionId,
            MimeType = mimeType,
            FileName = fileName,
            Size = bytes.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Content = bytes,
            CreatedAt = _time.GetUtcNow(),
        };
        await using (var context = await db.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            context.BinaryFiles.Add(entity);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var extension = Path.GetExtension(fileName ?? "").TrimStart('.');
        return new BinaryRef
        {
            Id = entity.Id,
            MimeType = mimeType,
            FileName = fileName,
            FileExtension = extension.Length > 0 ? extension.ToLowerInvariant() : null,
            FileSize = entity.Size,
            Storage = StorageName,
            Sha256 = entity.Sha256,
        };
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(BinaryRef reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await using var context = await db.CreateDbContextAsync(ct).ConfigureAwait(false);
        var content = await context.BinaryFiles.AsNoTracking()
            .Where(f => f.Id == reference.Id)
            .Select(f => f.Content)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return content is null ? throw new BinaryNotFoundException(reference.Id) : new MemoryStream(content, writable: false);
    }

    /// <summary>Прив'язує ще не прив'язані файли до виконання (після його створення).</summary>
    /// <param name="ids">Ключі файлів.</param>
    /// <param name="executionId">Виконання.</param>
    /// <param name="ct">Скасування.</param>
    public async Task AttachAsync(IReadOnlyCollection<string> ids, Guid executionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        await using var context = await db.CreateDbContextAsync(ct).ConfigureAwait(false);
        await context.BinaryFiles.Where(f => ids.Contains(f.Id) && f.ExecutionId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ExecutionId, executionId), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Видаляє ще не прив'язані файли (виконання не створилось).</summary>
    /// <param name="ids">Ключі файлів.</param>
    /// <param name="ct">Скасування.</param>
    public async Task DeleteUnattachedAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        await using var context = await db.CreateDbContextAsync(ct).ConfigureAwait(false);
        await context.BinaryFiles.Where(f => ids.Contains(f.Id) && f.ExecutionId == null).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}

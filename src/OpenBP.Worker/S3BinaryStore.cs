using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OpenBP.Engine;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker;

/// <summary>
/// <see cref="IBinaryStore"/> у S3-сумісному сховищі (F6-18, docs/architecture.md §6): об'єкт <c>&lt;рік&gt;/&lt;місяць&gt;/&lt;id&gt;</c>
/// у бакеті <see cref="S3StorageOptions.Bucket"/>, <see cref="BinaryRef.Storage"/> — <c>s3</c>. Як і
/// <see cref="LocalBinaryStore"/>, читання приймає лише ключі, які створює саме сховище: <c>BinaryRef</c> може прийти з даних
/// користувача (<c>pinData</c>, <c>startItems</c>), і підроблене посилання не повинно відкрити чужий об'єкт бакета.
/// Кілька воркерів з одним бакетом бачать файли одне одного.
/// </summary>
public sealed partial class S3BinaryStore : IBinaryStore, IDisposable
{
    /// <summary>Значення <see cref="BinaryRef.Storage"/> для файлів цього сховища.</summary>
    public const string StorageName = "s3";

    private const string Sha256Metadata = "sha256";

    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private volatile bool _bucketReady;

    /// <summary>Створює сховище з готовим клієнтом (клієнт закривається разом зі сховищем).</summary>
    /// <param name="client">Клієнт S3.</param>
    /// <param name="bucket">Бакет.</param>
    /// <param name="time">Годинник для каталогу рік/місяць; <c>null</c> — <see cref="TimeProvider.System"/>.</param>
    public S3BinaryStore(IAmazonS3 client, string bucket, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        _client = client;
        _bucket = bucket;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Створює сховище за налаштуваннями <c>Storage:S3</c>.</summary>
    /// <param name="options">Налаштування; <see cref="S3StorageOptions.Bucket"/> обов'язковий.</param>
    /// <param name="time">Годинник.</param>
    public static S3BinaryStore Create(S3StorageOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Bucket, "Storage:S3:Bucket");
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            // S3-сумісні сервіси не всі підтримують нові контрольні суми SDK — рахуємо їх лише там, де S3 вимагає.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
        else
        {
            config.ServiceURL = options.Endpoint;
            config.AuthenticationRegion = options.Region;
        }

        var client = string.IsNullOrEmpty(options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        return new S3BinaryStore(client, options.Bucket, time);
    }

    /// <inheritdoc />
    public async Task<BinaryRef> PutAsync(Stream content, string mimeType, string? fileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(mimeType);
        await EnsureBucketAsync(ct).ConfigureAwait(false);

        var id = Guid.NewGuid().ToString("N");
        var directory = _time.GetUtcNow().ToString("yyyy'/'MM", CultureInfo.InvariantCulture);

        // Розмір і SHA-256 потрібні до завантаження, а потік може бути одноразовим — буферизуємо в тимчасовий файл.
        var temp = Path.Combine(Path.GetTempPath(), $"openbp-s3-{id}");
        await using var buffer = new FileStream(
            temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(chunk, 0, read);
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        var size = buffer.Length;
        var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        buffer.Position = 0;

        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = $"{directory}/{id}",
            InputStream = buffer,
            AutoCloseStream = false,
            ContentType = mimeType,
        };
        request.Metadata[Sha256Metadata] = hash;
        await _client.PutObjectAsync(request, ct).ConfigureAwait(false);

        return new BinaryRef
        {
            Id = id,
            MimeType = mimeType,
            FileName = fileName,
            FileExtension = Path.GetExtension(fileName) is { Length: > 1 } extension ? extension[1..].ToLowerInvariant() : null,
            FileSize = size,
            Storage = StorageName,
            Directory = directory,
            Sha256 = hash,
        };
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(BinaryRef reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Storage != StorageName || !FileId().IsMatch(reference.Id) ||
            reference.Directory is null || !MonthDirectory().IsMatch(reference.Directory))
        {
            throw new BinaryNotFoundException(reference.Id);
        }

        try
        {
            var response = await _client.GetObjectAsync(_bucket, $"{reference.Directory}/{reference.Id}", ct).ConfigureAwait(false);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new BinaryNotFoundException(reference.Id);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
        _bucketLock.Dispose();
    }

    /// <summary>Створює бакет, якщо його немає (раз на процес).</summary>
    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady)
        {
            return;
        }

        await _bucketLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_bucketReady)
            {
                return;
            }

            try
            {
                await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, ct).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Conflict || ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
            {
                // Бакет уже є (створив інший воркер або адміністратор).
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                // Немає права створювати бакети — вважаємо, що бакет створено заздалегідь; помилку покаже запис.
            }

            _bucketReady = true;
        }
        finally
        {
            _bucketLock.Release();
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex FileId();

    [GeneratedRegex("^[0-9]{4}/[0-9]{2}$")]
    private static partial Regex MonthDirectory();
}

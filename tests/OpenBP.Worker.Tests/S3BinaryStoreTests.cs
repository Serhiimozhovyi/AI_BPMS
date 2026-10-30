using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Mcp.FakeServer;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// MinIO у Testcontainers для <see cref="S3BinaryStore"/> (F6-18). Офіційного образу <c>minio/minio</c> на Docker Hub більше
/// немає, тож береться збірка MinIO від Bitnami (<c>bitnamilegacy/minio</c>, той самий сервер MinIO).
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string Image = "bitnamilegacy/minio:2025.7.23";
    public const string AccessKey = "openbp";
    public const string SecretKey = "openbp-test-secret";

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithEnvironment("MINIO_ROOT_USER", AccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
        .WithPortBinding(9000, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/ready")))
        .Build();

    public string Endpoint => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";

    /// <summary>
    /// Старт контейнера і проба справжніми запитами: «health/ready» MinIO відповідає ще до того, як сервер стабільно
    /// приймає API-запити (перший запит тесту отримував «Client disconnected before response was ready»).
    /// Готово — коли кілька запитів поспіль пройшли без помилки.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        using var client = new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config { ServiceURL = Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var probe = "probe-" + Guid.NewGuid().ToString("N")[..12];
        var bucket = false;
        for (var ok = 0; ok < 5; await Task.Delay(200))
        {
            try
            {
                if (!bucket)
                {
                    await client.PutBucketAsync(probe);
                    bucket = true;
                }

                await client.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest { BucketName = probe, Key = $"k{ok}", ContentBody = "x" });
                ok++;
            }
            catch (Exception ex) when (ex is Amazon.Runtime.AmazonServiceException or HttpRequestException && DateTime.UtcNow < deadline)
            {
                ok = 0;
            }
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Налаштування на новий бакет — кожен тест у своєму.</summary>
    public S3StorageOptions Options(string? bucket = null) => new()
    {
        Endpoint = Endpoint,
        Bucket = bucket ?? "t-" + Guid.NewGuid().ToString("N")[..20],
        AccessKey = AccessKey,
        SecretKey = SecretKey,
    };
}

[CollectionDefinition(Name)]
public sealed class MinioCollection : ICollectionFixture<MinioFixture>
{
    public const string Name = "minio";
}

/// <summary>Сховище в S3 (MinIO): запис, читання, хеш, підроблені посилання, реєстрація у воркері.</summary>
[Collection(MinioCollection.Name)]
public sealed class S3BinaryStoreTests(MinioFixture minio)
{
    [Fact]
    public async Task Put_creates_bucket_and_object_and_read_returns_same_bytes()
    {
        var options = minio.Options();
        using var store = S3BinaryStore.Create(options, new FixedTime(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero)));
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 акт №42");

        var reference = await store.PutAsync(new NonSeekableStream(bytes), "application/pdf", "Акт 42.PDF", CancellationToken.None);

        Assert.Matches("^[0-9a-f]{32}$", reference.Id);
        Assert.Equal("2026/10", reference.Directory);
        Assert.Equal(S3BinaryStore.StorageName, reference.Storage);
        Assert.Equal("application/pdf", reference.MimeType);
        Assert.Equal("pdf", reference.FileExtension);
        Assert.Equal(bytes.Length, reference.FileSize);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), reference.Sha256);
        Assert.Equal(bytes, await ReadAllAsync(store, reference));

        // Об'єкт лежить у бакеті під ключем рік/місяць/id, з типом і хешем у метаданих.
        using var client = Client();
        var head = await client.GetObjectMetadataAsync(options.Bucket, $"2026/10/{reference.Id}");
        Assert.Equal("application/pdf", head.Headers.ContentType);
        Assert.Equal(reference.Sha256, head.Metadata["sha256"]);
    }

    [Fact]
    public async Task Read_of_missing_object_is_BinaryNotFound()
    {
        using var store = S3BinaryStore.Create(minio.Options());
        await store.PutAsync(new MemoryStream([1]), "application/octet-stream", null, CancellationToken.None);

        await Assert.ThrowsAsync<BinaryNotFoundException>(() => store.OpenReadAsync(
            new BinaryRef { Id = Guid.NewGuid().ToString("N"), Directory = "2026/10", Storage = "s3" }, CancellationToken.None));
    }

    [Theory]
    [InlineData("../secret", "2026/10", "s3")]
    [InlineData("secret", null, "s3")]
    [InlineData("0123456789abcdef0123456789abcdef", "..", "s3")]
    [InlineData("0123456789abcdef0123456789abcdef", "secret", "s3")]
    [InlineData("0123456789abcdef0123456789abcdef", null, "s3")]
    [InlineData("0123456789abcdef0123456789abcdef", "2026/10", "local")]
    [InlineData("0123456789abcdef0123456789abcdef", "2026/10", null)]
    public async Task Read_rejects_references_the_store_did_not_create(string id, string? directory, string? storage)
    {
        // У бакеті є «чужі» об'єкти з такими ключами, але посилання не своє — читати не можна.
        var options = minio.Options();
        using var store = S3BinaryStore.Create(options);
        await store.PutAsync(new MemoryStream([1]), "text/plain", null, CancellationToken.None);
        using var client = Client();
        foreach (var key in new[] { "secret", "0123456789abcdef0123456789abcdef", "secret/0123456789abcdef0123456789abcdef" })
        {
            await SeedAsync(client, options.Bucket, key, "secret");
        }

        await Assert.ThrowsAsync<BinaryNotFoundException>(() =>
            store.OpenReadAsync(new BinaryRef { Id = id, Directory = directory, Storage = storage }, CancellationToken.None));
    }

    [Fact]
    public async Task Put_retries_when_the_server_drops_the_request()
    {
        // MinIO під навантаженням (CI) відповідає на запис 499 «Client disconnected before response was ready» і нічого не
        // записує; SDK такі відповіді не повторює — сховище пробує ще раз з початку файлу.
        var options = minio.Options();
        var dropping = new DropFirstObjectPut();
        var client = new AmazonS3Client(MinioFixture.AccessKey, MinioFixture.SecretKey, new AmazonS3Config
        {
            ServiceURL = minio.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            HttpClientFactory = dropping,
        });
        using var store = new S3BinaryStore(client, options.Bucket!);
        var bytes = Encoding.UTF8.GetBytes("повтор після обриву");

        var reference = await store.PutAsync(new MemoryStream(bytes), "text/plain", "a.txt", CancellationToken.None);

        Assert.Equal(1, dropping.Dropped);
        Assert.Equal(bytes, await ReadAllAsync(store, reference));
    }

    [Fact]
    public async Task Worker_writes_to_s3_and_still_reads_local_files_when_both_are_configured()
    {
        var root = Directory.CreateTempSubdirectory("openbp-files-").FullName;
        try
        {
            var options = minio.Options();
            var local = await new LocalBinaryStore(root).PutAsync(new MemoryStream([7, 7]), "image/png", "old.png", CancellationToken.None);

            IServiceProvider Build(string? bucket, string? path)
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = "Host=localhost;Database=none",
                    ["Storage:LocalPath"] = path,
                    ["Storage:S3:Endpoint"] = options.Endpoint,
                    ["Storage:S3:Bucket"] = bucket,
                    ["Storage:S3:AccessKey"] = MinioFixture.AccessKey,
                    ["Storage:S3:SecretKey"] = MinioFixture.SecretKey,
                }).Build();
                return new ServiceCollection().AddLogging().AddOpenBpWorker(configuration).BuildServiceProvider();
            }

            await using (var onlyS3 = (ServiceProvider)Build(options.Bucket, null))
            {
                var store = onlyS3.GetRequiredService<IBinaryStore>();
                var written = await store.PutAsync(new MemoryStream([1, 2, 3]), "text/plain", "a.txt", CancellationToken.None);
                Assert.Equal("s3", written.Storage);
                await Assert.ThrowsAsync<BinaryNotFoundException>(() => store.OpenReadAsync(local, CancellationToken.None));
            }

            await using var both = (ServiceProvider)Build(options.Bucket, root);
            var routed = both.GetRequiredService<IBinaryStore>();
            var s3 = await routed.PutAsync(new MemoryStream([4, 5]), "text/plain", null, CancellationToken.None);
            Assert.Equal("s3", s3.Storage);
            Assert.Equal([4, 5], await ReadAllAsync(routed, s3));
            Assert.Equal([7, 7], await ReadAllAsync(routed, local));
            Assert.Equal([7, 7], await ReadAllAsync(routed, new BinaryRef { Id = local.Id, Directory = local.Directory, Storage = null }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static async Task<byte[]> ReadAllAsync(IBinaryStore store, BinaryRef reference)
    {
        await using var read = await store.OpenReadAsync(reference, CancellationToken.None);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        return copy.ToArray();
    }

    /// <summary>Перший запис об'єкта (PUT з ключем) отримує 499 від «сервера», не дійшовши до MinIO.</summary>
    private sealed class DropFirstObjectPut : Amazon.Runtime.HttpClientFactory
    {
        private int _dropped;

        public int Dropped => _dropped;

        public override HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig clientConfig) => new(new Handler(this));

        private sealed class Handler(DropFirstObjectPut owner) : DelegatingHandler(new HttpClientHandler())
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var isObjectPut = request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.Trim('/').Contains('/', StringComparison.Ordinal);
                if (isObjectPut && Interlocked.CompareExchange(ref owner._dropped, 1, 0) == 0)
                {
                    if (request.Content is not null)
                    {
                        await request.Content.CopyToAsync(Stream.Null, cancellationToken);
                    }

                    return new HttpResponseMessage((HttpStatusCode)499)
                    {
                        RequestMessage = request,
                        Content = new StringContent(
                            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>ClientDisconnected</Code>" +
                            "<Message>Client disconnected before response was ready</Message></Error>",
                            Encoding.UTF8,
                            "application/xml"),
                    };
                }

                return await base.SendAsync(request, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Кладе «чужий» об'єкт напряму через SDK. MinIO під навантаженням (CI) іноді відповідає на запис 499 «Client disconnected
    /// before response was ready» і нічого не пише, а SDK такі відповіді не повторює — тому повторюємо самі (запис ідемпотентний).
    /// </summary>
    private static async Task SeedAsync(AmazonS3Client client, string bucket, string key, string body)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest { BucketName = bucket, Key = key, ContentBody = body });
                return;
            }
            catch (AmazonS3Exception) when (attempt < 4)
            {
                await Task.Delay(200 * attempt);
            }
        }
    }

    private AmazonS3Client Client() => new(
        MinioFixture.AccessKey,
        MinioFixture.SecretKey,
        new AmazonS3Config { ServiceURL = minio.Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Одноразовий потік (як тіло HTTP-відповіді): сховище не може перемотати його назад.</summary>
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}

/// <summary>Два воркери з одним бакетом бачать файли одне одного (F6-18).</summary>
[Collection(PostgresCollection.Name)]
public sealed class S3BinaryStoreWorkerTests(PostgresFixture postgres, MinioFixture minio) : IClassFixture<MinioFixture>
{
    [Fact]
    public async Task Two_workers_share_files_through_one_bucket()
    {
        var options = minio.Options();
        using var storeA = S3BinaryStore.Create(options);
        using var storeB = S3BinaryStore.Create(options);
        await using var workerA = await WorkerHarness.CreateAsync(postgres, binary: storeA);
        await using var workerB = workerA.SecondWorker(storeB);
        var fake = Workflows.FakeServer("Fake");
        await using (var db = workerA.Db.CreateDbContext())
        {
            db.McpServers.Add(fake);
            await db.SaveChangesAsync();
        }

        var (workflowId, versionId) = await workerA.SaveWorkflowAsync(WorkflowJson.Deserialize($$$"""
            {
              "formatVersion": 1,
              "name": "Картинка в S3",
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_pixel", "name": "Картинка", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "serverId": "{{{fake.Id}}}", "toolName": "pixel", "arguments": {} } }
              ],
              "connections": [ { "from": { "node": "n_start" }, "to": { "node": "n_pixel" } } ]
            }
            """));

        async Task<BinaryRef> RunAsync(WorkerHarness worker)
        {
            var executionId = await worker.LaunchAsync(workflowId, versionId);
            Assert.True(await worker.ProcessNextAsync());
            var execution = await worker.ExecutionAsync(executionId);
            Assert.True(execution.Status == ExecutionStatuses.Success, execution.Error);
            var output = JsonNode.Parse((await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_pixel").Output!)![0]![0]!;
            return output["binary"]!["data"].Deserialize<BinaryRef>(WorkflowJson.Options)!;
        }

        var fromA = await RunAsync(workerA);
        var fromB = await RunAsync(workerB);

        Assert.Equal("s3", fromA.Storage);
        Assert.NotEqual(fromA.Id, fromB.Id);
        Assert.Equal(FakeTools.PixelBytes, await S3BinaryStoreTests.ReadAllAsync(storeB, fromA));
        Assert.Equal(FakeTools.PixelBytes, await S3BinaryStoreTests.ReadAllAsync(storeA, fromB));
    }
}

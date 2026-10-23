using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Mcp.FakeServer;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>Файлове сховище в локальній папці: збереження, читання, захист від шляхів поза коренем, реєстрація у воркері.</summary>
public sealed class LocalBinaryStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openbp-files-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Put_writes_file_under_month_folder_and_read_returns_same_bytes()
    {
        var store = new LocalBinaryStore(_root, new FixedTime(new DateTimeOffset(2026, 10, 23, 22, 0, 0, TimeSpan.Zero)));
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 рахунок №17");

        var reference = await store.PutAsync(new MemoryStream(bytes), "application/pdf", "Рахунок 17.PDF", CancellationToken.None);

        Assert.Matches("^[0-9a-f]{32}$", reference.Id);
        Assert.Equal("2026/10", reference.Directory);
        Assert.Equal(LocalBinaryStore.StorageName, reference.Storage);
        Assert.Equal("application/pdf", reference.MimeType);
        Assert.Equal("Рахунок 17.PDF", reference.FileName);
        Assert.Equal("pdf", reference.FileExtension);
        Assert.Equal(bytes.Length, reference.FileSize);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), reference.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(_root, "2026", "10", reference.Id)));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));

        await using var read = await store.OpenReadAsync(reference, CancellationToken.None);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task Read_of_missing_file_is_BinaryNotFound()
    {
        var store = new LocalBinaryStore(_root);

        await Assert.ThrowsAsync<BinaryNotFoundException>(() =>
            store.OpenReadAsync(new BinaryRef { Id = Guid.NewGuid().ToString("N"), Directory = "2026/10", Storage = "local" }, CancellationToken.None));
    }

    [Theory]
    [InlineData("../../etc/passwd", "2026/10", "local")]
    [InlineData("0123456789abcdef0123456789abcdef", "../..", "local")]
    [InlineData("0123456789abcdef0123456789abcdef", "/etc", "local")]
    [InlineData("0123456789abcdef0123456789abcdef", null, "s3")]
    public async Task Read_rejects_references_the_store_did_not_create(string id, string? directory, string storage)
    {
        // Файл з таким ключем навіть існує поруч, але посилання не своє — читати не можна.
        await File.WriteAllTextAsync(Path.Combine(_root, "0123456789abcdef0123456789abcdef"), "secret");
        var store = new LocalBinaryStore(Path.Combine(_root, "files"));

        await Assert.ThrowsAsync<BinaryNotFoundException>(() =>
            store.OpenReadAsync(new BinaryRef { Id = id, Directory = directory, Storage = storage }, CancellationToken.None));
    }

    [Fact]
    public void Worker_registers_local_store_only_when_LocalPath_is_set()
    {
        static IServiceProvider Build(string? path)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=none",
                ["Storage:LocalPath"] = path,
            }).Build();
            return new ServiceCollection().AddLogging().AddOpenBpWorker(configuration).BuildServiceProvider();
        }

        Assert.IsType<LocalBinaryStore>(Build(_root).GetService<IBinaryStore>());
        Assert.Null(Build(null).GetService<IBinaryStore>());
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>Воркер зберігає файл, який повернув MCP-tool, у <see cref="LocalBinaryStore"/>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class LocalBinaryStoreWorkerTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openbp-files-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Mcp_tool_image_result_is_saved_to_local_store()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres, binary: new LocalBinaryStore(_root));
        var fake = Workflows.FakeServer("Fake");
        await using (var db = worker.Db.CreateDbContext())
        {
            db.McpServers.Add(fake);
            await db.SaveChangesAsync();
        }

        var (workflowId, versionId) = await worker.SaveWorkflowAsync(WorkflowJson.Deserialize($$"""
            {
              "formatVersion": 1,
              "name": "Картинка",
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_pixel", "name": "Картинка", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "serverId": "{{fake.Id}}", "toolName": "pixel", "arguments": {} } }
              ],
              "connections": [ { "from": { "node": "n_start" }, "to": { "node": "n_pixel" } } ]
            }
            """));
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        await worker.StartAsync();
        var execution = await worker.WaitForFinalAsync(executionId);

        Assert.True(execution.Status == ExecutionStatuses.Success, execution.Error);
        var output = JsonNode.Parse((await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_pixel").Output!)![0]![0]!;
        var reference = output["binary"]!["data"]!;
        Assert.Equal("local", (string?)reference["storage"]);
        Assert.Equal("image/png", (string?)reference["mimeType"]);
        var path = Path.Combine(_root, (string)reference["directory"]!, (string)reference["id"]!);
        Assert.Equal(FakeTools.PixelBytes, await File.ReadAllBytesAsync(path));
    }
}

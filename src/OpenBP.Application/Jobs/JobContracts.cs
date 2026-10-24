using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Jobs;

/// <summary>Значення <c>jobs.kind</c> (docs/architecture.md §10.4). Воркер бере лише ті, які вміє обробляти.</summary>
public static class JobKinds
{
    /// <summary>Виконати сценарій; payload — <see cref="ExecutionJobPayload"/>.</summary>
    public const string Execution = "execution";

    /// <summary>Спрацювання розкладу вузла <c>mcp.poll</c>; payload — <see cref="PollJobPayload"/>.</summary>
    public const string Poll = "poll";
}

/// <summary>
/// Payload job <see cref="JobKinds.Execution"/>: усе, що треба воркеру, щоб виконати сценарій, **без секретів**
/// (credentials воркер читає сам). Рядок <c>executions</c> з цим <see cref="ExecutionId"/> уже існує в стані <c>queued</c>.
/// </summary>
public sealed record ExecutionJobPayload
{
    /// <summary><c>executions.id</c>.</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary><c>workflows.id</c>.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary><c>workflow_versions.id</c> — визначення береться звідси, а не з поточної версії сценарію.</summary>
    public required Guid VersionId { get; init; }

    /// <summary>Режим запуску.</summary>
    public required ExecutionMode Mode { get; init; }

    /// <summary>Тригер, з якого стартувати; <c>null</c> — усі вузли без вхідних ребер.</summary>
    public string? StartNodeId { get; init; }

    /// <summary>Items тригера (тіло webhook, нові записи <c>mcp.poll</c>); стартовий вузол тоді не виконується.</summary>
    public IReadOnlyList<Item>? StartItems { get; init; }

    /// <summary>«Виконати вузол» (<see cref="ExecutionRequest.DestinationNodeId"/>); <c>null</c> — увесь сценарій.</summary>
    public string? DestinationNodeId { get; init; }

    /// <summary>Виходи вузлів попереднього тестового запуску (<see cref="ExecutionRequest.RunData"/>).</summary>
    public IReadOnlyDictionary<string, Item[][]>? RunData { get; init; }

    /// <summary>
    /// Група паралельності (<c>JobQueue.ConcurrencyKeyField</c>): id сценарію, якщо на запуск діє
    /// <c>settings.concurrency</c> з <c>onLimit: queue</c> (F6-06); інакше <c>null</c>.
    /// </summary>
    public string? ConcurrencyKey { get; init; }

    /// <summary>Ліміт одночасних виконань групи <see cref="ConcurrencyKey"/> (<c>JobQueue.ConcurrencyMaxField</c>).</summary>
    public int? ConcurrencyMax { get; init; }
}

/// <summary>Payload job <see cref="JobKinds.Poll"/>: який вузол опитати. Стан вузла воркер читає з <c>node_state</c>.</summary>
public sealed record PollJobPayload
{
    /// <summary><c>workflows.id</c>; опитується поточна версія активного сценарію.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>Id вузла <c>mcp.poll</c>.</summary>
    public required string NodeId { get; init; }
}

/// <summary>(Де)серіалізація payload job'ів: camelCase, enum як рядки, items — за <see cref="WorkflowJson.Options"/>.</summary>
public static class JobJson
{
    /// <summary>Налаштування серіалізації payload.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Серіалізує payload для <c>JobQueue.EnqueueAsync</c>.</summary>
    /// <typeparam name="T">Тип payload.</typeparam>
    /// <param name="payload">Payload.</param>
    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>Розбирає payload з <c>ClaimedJob.Payload</c>.</summary>
    /// <typeparam name="T">Тип payload за <c>kind</c>.</typeparam>
    /// <param name="json">JSON.</param>
    /// <exception cref="JsonException">JSON не відповідає типу.</exception>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Payload порожній, очікувався {typeof(T).Name}.");

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(WorkflowJson.Options) { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

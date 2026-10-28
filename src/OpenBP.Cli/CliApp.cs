using System.Text.Json;
using OpenBP.Ai;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Testing;
using OpenBP.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Nodes;
using OpenBP.Nodes.Core;
using OpenBP.Shared.Workflow;

namespace OpenBP.Cli;

/// <summary>
/// <c>openbp run &lt;file.json&gt; [--pin] [--servers servers.json] [--credentials creds.json]</c> — виконання сценарію без БД
/// (docs/architecture.md §1): in-memory store, <c>core.*</c> і <c>mcp.*</c> вузли, Jint-вирази.
/// Друкує input/output кожного вузла. Коди виходу: 0 — успіх, 1 — виконання завершилось не успіхом, 2 — помилка запуску.
/// <c>openbp export | import | activate | export-project | import-project</c> — робота з запущеним OpenBP через REST для CI/CD (<see cref="RemoteCommands"/>):
/// 1 — імпорт без відповідників, 2 — помилка аргументів чи відповідь OpenBP з помилкою.
/// </summary>
public static class CliApp
{
    /// <summary>Код виходу: сценарій виконано успішно.</summary>
    public const int Ok = 0;

    /// <summary>Код виходу: виконання завершилось помилкою, скасуванням чи таймаутом.</summary>
    public const int ExecutionFailed = 1;

    /// <summary>Код виходу: неправильні аргументи, файл чи сценарій.</summary>
    public const int UsageError = 2;

    /// <summary>Найбільше відновлень після очікувань за один прогін.</summary>
    public const int MaxWaits = 10_000;

    private const string Usage = """
        Використання:
          openbp run <file.json> [--pin] [--servers servers.json] [--credentials creds.json]
          openbp export <id> | --all [--out dir] [--url URL] [--api-key KEY]
          openbp import <file.json> [--map map.json] [--url URL] [--api-key KEY]
          openbp activate <id> [--url URL] [--api-key KEY]
          openbp export-project <id> [--rows таблиця,таблиця] [--out file.json] [--url URL] [--api-key KEY]
          openbp import-project <bundle.json> [--map map.json] [--project id] [--name назва] [--url URL] [--api-key KEY]
          openbp draft-eval --workflow <id> [--cases dir] [--model назва] [--only частина-id] [--out report.md] [--url URL] [--api-key KEY]

          --pin        ручний тестовий запуск: вузли з pinData не виконуються, їхній вихід — закріплені items
          --servers    JSON з MCP-серверами, на які посилаються вузли mcp.* (serverId)
          --credentials JSON { "<credentialId>": { … } } з даними credentials для вузлів (напр. ai.llm)
          --out        export: тека для файлів експорту (<id>.json), типово поточна; export-project: файл шаблону, типово stdout
          --rows       export-project: таблиці, рядки яких додати в шаблон (чутливі колонки не експортуються)
          --project    import-project: наявний проєкт для оновлення; типово створюється новий
          --name       import-project: назва нового проєкту замість назви з шаблону
          --map        JSON { "connectors": { "<ref>": "<id>" }, "credentials": { … }, "workflows": { … } }
          --workflow   draft-eval: сценарій, у який «будуємо» (його вміст не змінюється)
          --cases      draft-eval: тека з описами; типово tests/drafter-cases
          --model      draft-eval: підпис моделі в звіті; типово — модель з налаштувань помічника
          --url        адреса OpenBP; типово змінна OPENBP_URL
          --api-key    API-ключ (Bearer); типово змінна OPENBP_API_KEY — у CI беріть його із секрету
        """;

    /// <summary>Розбирає аргументи і виконує команду.</summary>
    /// <param name="args">Аргументи командного рядка.</param>
    /// <param name="stdout">Вивід звіту.</param>
    /// <param name="stderr">Вивід помилок запуску.</param>
    /// <param name="ct">Скасування (Ctrl+C).</param>
    /// <returns>Код виходу.</returns>
    public static Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct) =>
        RunAsync(args, stdout, stderr, http: null, ct);

    /// <summary>Те саме з власним HTTP-обробником для команд проти OpenBP (тести — <c>WebApplicationFactory</c>).</summary>
    /// <param name="args">Аргументи командного рядка.</param>
    /// <param name="stdout">Вивід звіту.</param>
    /// <param name="stderr">Вивід помилок запуску.</param>
    /// <param name="http">Обробник HTTP; <c>null</c> — звичайний.</param>
    /// <param name="ct">Скасування (Ctrl+C).</param>
    /// <returns>Код виходу.</returns>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, HttpMessageHandler? http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        try
        {
            if (args.Length > 0 && args[0] == DraftEval.Name)
            {
                return await DraftEval.RunAsync(args, stdout, http, ct).ConfigureAwait(false);
            }

            if (args.Length > 0 && RemoteCommands.Names.Contains(args[0]))
            {
                return await RemoteCommands.RunAsync(args, stdout, http, ct).ConfigureAwait(false);
            }

            var options = RunOptions.Parse(args);
            return await RunWorkflowAsync(options, stdout, ct).ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            if (ex.ShowUsage)
            {
                await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            }

            return UsageError;
        }
    }

    private static async Task<int> RunWorkflowAsync(RunOptions options, TextWriter stdout, CancellationToken ct)
    {
        var workflow = LoadWorkflow(options.WorkflowPath);
        var servers = options.ServersPath is null ? [] : ServersFile.Load(options.ServersPath);

        await using var mcp = new McpServerRegistry();
        foreach (var server in servers)
        {
            await mcp.RegisterAsync(server).ConfigureAwait(false);
        }

        var credentials = options.CredentialsPath is null ? new FakeCredentialProvider() : CredentialsFile.Load(options.CredentialsPath);

        // Таймаутом запиту до моделі керує сам вузол (timeoutSec), тож HttpClient без власного таймауту.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // Очікування (core.wait v2/v3, mcp.waitFor) CLI не відбуває: годинник перемотується до часу відновлення;
        // webhook/form без HTTP-сервера йдуть виходом timeout.
        var clock = new CliClock(DateTimeOffset.UtcNow);
        var registry = new InMemoryNodeRegistry();
        foreach (var node in CoreNodes.All(clock, CliDataTables.Create(workflow)).Concat(McpNodes.All(mcp, clock)).Concat(AiNodes.All(http, mcp)))
        {
            registry.Register(node);
        }

        PinOnlyNode.RegisterMissing(registry, n => registry.Register(n));

        var mode = options.Pin ? ExecutionMode.Manual : ExecutionMode.Trigger;
        var store = new InMemoryExecutionStore();
        var executionId = Guid.NewGuid();
        store.Register(new ExecutionSnapshot
        {
            Id = executionId,
            WorkflowId = Path.GetFileNameWithoutExtension(options.WorkflowPath),
            Workflow = workflow,
            Mode = mode,
            StartedAt = clock.GetUtcNow(),
        });

        var executor = new WorkflowExecutor(
            registry,
            store,
            new JintExpressionEvaluator(),
            credentials,
            new InMemoryBinaryStore(),
            new InMemoryExecutionLogger(),
            clock);

        var request = new ExecutionRequest { ExecutionId = executionId, Workflow = workflow, Mode = mode };
        ExecutionResult result;
        var waits = 0;
        try
        {
            result = await executor.RunAsync(request, ct).ConfigureAwait(false);
            while (result is { Status: ExecutionStatus.Waiting, ResumeAt: { } resumeAt })
            {
                if (++waits > MaxWaits)
                {
                    throw new CliException($"Сценарій чекав понад {MaxWaits} разів — схоже на нескінченне очікування.");
                }

                clock.AdvanceTo(resumeAt);
                var snapshot = await store.LoadAsync(executionId, ct).ConfigureAwait(false)
                    ?? throw new CliException("Знімок виконання зник під час очікування.");
                result = await executor.ResumeAsync(snapshot, request, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is EngineException or ArgumentException)
        {
            throw new CliException($"Сценарій не можна виконати: {ex.Message}");
        }

        await RunReport.WriteAsync(stdout, workflow, result, mode).ConfigureAwait(false);
        if (waits > 0)
        {
            await stdout.WriteLineAsync($"Очікувань: {waits}; час CLI перемотано до {clock.GetUtcNow():yyyy-MM-ddTHH:mm:ssZ} (у OpenBP виконання чекало б у стані waiting).").ConfigureAwait(false);
        }

        return result.Status == ExecutionStatus.Success ? Ok : ExecutionFailed;
    }

    private static WorkflowDefinition LoadWorkflow(string path)
    {
        try
        {
            return WorkflowJson.Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CliException($"Не вдалося прочитати сценарій {path}: {ex.Message}");
        }
    }

    /// <summary>Аргументи <c>openbp run</c>.</summary>
    private sealed record RunOptions(string WorkflowPath, bool Pin, string? ServersPath, string? CredentialsPath)
    {
        public static RunOptions Parse(string[] args)
        {
            if (args.Length == 0 || args[0] != "run")
            {
                throw new CliException(args.Length == 0 ? "Не вказано команду." : $"Невідома команда «{args[0]}».", showUsage: true);
            }

            string? file = null;
            string? servers = null;
            string? credentials = null;
            var pin = false;
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--pin":
                        pin = true;
                        break;
                    case "--servers" when i + 1 < args.Length:
                        servers = args[++i];
                        break;
                    case "--servers":
                        throw new CliException("Після --servers очікується шлях до файлу.", showUsage: true);
                    case "--credentials" when i + 1 < args.Length:
                        credentials = args[++i];
                        break;
                    case "--credentials":
                        throw new CliException("Після --credentials очікується шлях до файлу.", showUsage: true);
                    case var a when a.StartsWith("--", StringComparison.Ordinal):
                        throw new CliException($"Невідомий параметр «{a}».", showUsage: true);
                    case var a when file is null:
                        file = a;
                        break;
                    default:
                        throw new CliException($"Зайвий аргумент «{args[i]}».", showUsage: true);
                }
            }

            return file is null
                ? throw new CliException("Не вказано файл сценарію.", showUsage: true)
                : new RunOptions(file, pin, servers, credentials);
        }
    }
}

/// <summary>Помилка запуску CLI: повідомлення для користувача, код виходу <see cref="CliApp.UsageError"/>.</summary>
internal sealed class CliException(string message, bool showUsage = false) : Exception(message)
{
    public bool ShowUsage { get; } = showUsage;
}

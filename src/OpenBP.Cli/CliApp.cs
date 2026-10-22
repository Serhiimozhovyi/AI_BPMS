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
/// </summary>
public static class CliApp
{
    /// <summary>Код виходу: сценарій виконано успішно.</summary>
    public const int Ok = 0;

    /// <summary>Код виходу: виконання завершилось помилкою, скасуванням чи таймаутом.</summary>
    public const int ExecutionFailed = 1;

    /// <summary>Код виходу: неправильні аргументи, файл чи сценарій.</summary>
    public const int UsageError = 2;

    private const string Usage = """
        Використання: openbp run <file.json> [--pin] [--servers servers.json] [--credentials creds.json]

          --pin        ручний тестовий запуск: вузли з pinData не виконуються, їхній вихід — закріплені items
          --servers    JSON з MCP-серверами, на які посилаються вузли mcp.* (serverId)
          --credentials JSON { "<credentialId>": { … } } з даними credentials для вузлів (напр. ai.llm)
        """;

    /// <summary>Розбирає аргументи і виконує команду.</summary>
    /// <param name="args">Аргументи командного рядка.</param>
    /// <param name="stdout">Вивід звіту.</param>
    /// <param name="stderr">Вивід помилок запуску.</param>
    /// <param name="ct">Скасування (Ctrl+C).</param>
    /// <returns>Код виходу.</returns>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        try
        {
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
        var registry = new InMemoryNodeRegistry();
        foreach (var node in CoreNodes.All().Concat(McpNodes.All(mcp)).Concat(AiNodes.All(http)))
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
            StartedAt = DateTimeOffset.UtcNow,
        });

        var executor = new WorkflowExecutor(
            registry,
            store,
            new JintExpressionEvaluator(),
            credentials,
            new InMemoryBinaryStore(),
            new InMemoryExecutionLogger());

        ExecutionResult result;
        try
        {
            result = await executor.RunAsync(
                new ExecutionRequest { ExecutionId = executionId, Workflow = workflow, Mode = mode },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EngineException or ArgumentException)
        {
            throw new CliException($"Сценарій не можна виконати: {ex.Message}");
        }

        await RunReport.WriteAsync(stdout, workflow, result, mode).ConfigureAwait(false);
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

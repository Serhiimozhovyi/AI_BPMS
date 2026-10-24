using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Application.Assistant;

/// <summary>Стани пояснення сценарію (<c>workflow_explanations.status</c>).</summary>
public static class WorkflowExplanationStatus
{
    /// <summary>Пояснення ще не замовлялося.</summary>
    public const string None = "none";

    /// <summary>Чекає воркера (після збереження — з паузою, щоб автозбереження не генерувало його на кожну правку).</summary>
    public const string Pending = "pending";

    /// <summary>Готове.</summary>
    public const string Ready = "ready";

    /// <summary>Модель відповіла помилкою — текст у <see cref="WorkflowExplanation.Error"/>.</summary>
    public const string Failed = "failed";

    /// <summary>Помічник не налаштований — пояснювати нема чим.</summary>
    public const string NotConfigured = "not_configured";
}

/// <summary>Пояснення сценарію простими словами (<c>GET /workflows/{id}/explanation</c>, F3-72).</summary>
/// <param name="Status">Стан — <see cref="WorkflowExplanationStatus"/>.</param>
/// <param name="Version">Версія сценарію, до якої належить <see cref="Text"/>; <c>null</c> — тексту ще немає.</param>
/// <param name="CurrentVersion">Поточна версія (чернетка) сценарію.</param>
/// <param name="Text">Пояснення.</param>
/// <param name="Error">Чому пояснення не вдалося.</param>
/// <param name="UpdatedAt">Коли змінився стан.</param>
public sealed record WorkflowExplanation(
    string Status,
    int? Version,
    int CurrentVersion,
    string? Text,
    string? Error,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>Текст належить старішій версії сценарію.</summary>
    public bool Stale => Text is not null && Version != CurrentVersion;
}

/// <summary>Пояснення сценарію (F3-72): читає viewer, замовляє заново editor.</summary>
public interface IWorkflowExplanationService
{
    /// <summary>Поточне пояснення сценарію.</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    Task<WorkflowExplanation> GetAsync(Guid workflowId, CancellationToken ct);

    /// <summary>Замовляє пояснення поточної версії одразу, навіть якщо сценарій не змінювався.</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    Task<WorkflowExplanation> RequestAsync(Guid workflowId, CancellationToken ct);
}

/// <summary>Реалізація <see cref="IWorkflowExplanationService"/>.</summary>
public sealed class WorkflowExplanationService(OpenBpDbContext db, ICurrentUser currentUser, TimeProvider time) : IWorkflowExplanationService
{
    private const string Entity = "workflow";

    private readonly ProjectAccess _access = new(db, currentUser);

    /// <inheritdoc />
    public async Task<WorkflowExplanation> GetAsync(Guid workflowId, CancellationToken ct)
    {
        var current = await CurrentVersionAsync(workflowId, UserRole.Viewer, ct);
        var row = await db.WorkflowExplanations.AsNoTracking().SingleOrDefaultAsync(e => e.WorkflowId == workflowId, ct);
        return ToDto(row, current);
    }

    /// <inheritdoc />
    public async Task<WorkflowExplanation> RequestAsync(Guid workflowId, CancellationToken ct)
    {
        var current = await CurrentVersionAsync(workflowId, UserRole.Editor, ct);
        await WorkflowExplanationQueue.ScheduleAsync(db, workflowId, current, time.GetUtcNow(), TimeSpan.Zero, force: true, ct);
        await db.SaveChangesAsync(ct);
        return ToDto(await db.WorkflowExplanations.AsNoTracking().SingleAsync(e => e.WorkflowId == workflowId, ct), current);
    }

    private async Task<int> CurrentVersionAsync(Guid workflowId, UserRole minimum, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var workflow = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == workflowId, ct)
                       ?? throw new NotFoundException(Entity, workflowId);
        await _access.RequireAsync(workflow.GroupId, minimum, Entity, workflowId, ct);
        return await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.Id == workflow.CurrentVersionId)
            .Select(v => v.Version)
            .SingleAsync(ct);
    }

    private static WorkflowExplanation ToDto(WorkflowExplanationEntity? row, int current) => row is null
        ? new WorkflowExplanation(WorkflowExplanationStatus.None, null, current, null, null, null)
        : new WorkflowExplanation(row.Status, row.Text is null ? null : row.Version, current, row.Text, row.Error, row.UpdatedAt);
}

/// <summary>Постановка пояснення в чергу (<see cref="JobKinds.Explain"/>) — у тій самій транзакції, що й збереження.</summary>
internal static class WorkflowExplanationQueue
{
    /// <summary>Пауза після збереження: автозбереження пише версію на кожну правку, пояснюється лише остання.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Позначає пояснення як <c>pending</c> і додає job до контексту (зберігає викликач). Помічник не налаштований —
    /// стан <c>not_configured</c> без job'а.
    /// </summary>
    /// <param name="db">Контекст збереження.</param>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="version">Номер нової поточної версії.</param>
    /// <param name="now">Час збереження.</param>
    /// <param name="delay">Пауза перед поясненням.</param>
    /// <param name="force">Пояснити заново, навіть якщо визначення не змінилось.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task ScheduleAsync(
        OpenBpDbContext db, Guid workflowId, int version, DateTimeOffset now, TimeSpan delay, bool force, CancellationToken ct)
    {
        var row = await db.WorkflowExplanations.SingleOrDefaultAsync(e => e.WorkflowId == workflowId, ct);
        if (row is null)
        {
            row = new WorkflowExplanationEntity { WorkflowId = workflowId, Version = version };
            db.WorkflowExplanations.Add(row);
        }

        row.Error = null;
        row.UpdatedAt = now;
        if (!(await AssistantSettingsService.LoadAsync(db, ct)).Configured)
        {
            row.Status = WorkflowExplanationStatus.NotConfigured;
            return;
        }

        row.Status = WorkflowExplanationStatus.Pending;
        if (force)
        {
            row.DefinitionHash = null;
        }

        db.Jobs.Add(new JobEntity
        {
            Id = SequentialGuid.Next(),
            Kind = JobKinds.Explain,
            Payload = JobJson.Serialize(new ExplainJobPayload { WorkflowId = workflowId, Version = version }),
            RunAt = (now + delay).ToUniversalTime(),
        });
    }
}

/// <summary>Чим закінчився job пояснення.</summary>
public enum ExplainOutcome
{
    /// <summary>Сценарій видалено або вже збережено новішу версію — пояснить наступний job.</summary>
    Skipped,

    /// <summary>Значуще визначення не змінилося (пересунули вузли) — пояснення лишилося, версія оновилась.</summary>
    Unchanged,

    /// <summary>Пояснення згенеровано.</summary>
    Ready,

    /// <summary>Модель відповіла помилкою.</summary>
    Failed,

    /// <summary>Помічник не налаштований.</summary>
    NotConfigured,
}

/// <summary>
/// Генерує пояснення сценарію простими словами (F3-72) моделлю помічника з «Налаштувань → Помічник». Працює без
/// користувача (воркер, job <see cref="JobKinds.Explain"/>). Модель отримує визначення без координат, нотаток і
/// <c>pinData</c> (тестові дані можуть бути реальними) і назви конекторів; значень credentials — ні.
/// </summary>
public sealed class WorkflowExplainer(IDbContextFactory<OpenBpDbContext> contexts, CredentialEncryptionKey key, LlmChat chat, TimeProvider time)
{
    /// <summary>Довгі рядки параметрів (код, промпти) обрізаються до стількох символів.</summary>
    private const int MaxStringChars = 3000;

    /// <summary>Пояснення великого сценарію довше за звичайну відповідь помічника.</summary>
    private const int MaxTokens = 8000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    /// <summary>Кирилиця без <c>\uXXXX</c> — модель читає назви кроків як є.</summary>
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Пояснює версію <paramref name="version"/> сценарію, якщо вона досі поточна.</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="version">Версія з job'а.</param>
    /// <param name="ct">Скасування (зупинка воркера).</param>
    public async Task<ExplainOutcome> ExplainAsync(Guid workflowId, int version, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var workflow = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null)
        {
            return ExplainOutcome.Skipped;
        }

        var current = await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.Id == workflow.CurrentVersionId)
            .Select(v => new { v.Version, v.Definition })
            .SingleOrDefaultAsync(ct);
        if (current is null || current.Version != version)
        {
            return ExplainOutcome.Skipped;
        }

        var row = await db.WorkflowExplanations.SingleOrDefaultAsync(e => e.WorkflowId == workflowId, ct);
        if (row is null)
        {
            row = new WorkflowExplanationEntity { WorkflowId = workflowId, Version = version };
            db.WorkflowExplanations.Add(row);
        }

        var essence = Essence(JsonNode.Parse(current.Definition)!.AsObject(), workflow.Name);
        var text = essence.ToJsonString(Readable);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        row.UpdatedAt = time.GetUtcNow();
        if (row.Text is not null && row.DefinitionHash == hash)
        {
            row.Version = version;
            row.Status = WorkflowExplanationStatus.Ready;
            await db.SaveChangesAsync(ct);
            return ExplainOutcome.Unchanged;
        }

        var settings = await AssistantSettingsService.LoadAsync(db, ct);
        var credential = settings.Configured ? await new CredentialStore(contexts, key).GetDataAsync(settings.CredentialId!.Value, ct) : null;
        if (credential is null)
        {
            row.Status = WorkflowExplanationStatus.NotConfigured;
            await db.SaveChangesAsync(ct);
            return ExplainOutcome.NotConfigured;
        }

        var prompt = new StringBuilder()
            .Append("Сценарій «").Append(workflow.Name).AppendLine("»:")
            .AppendLine(text);
        var connectors = await ConnectorNamesAsync(db, essence, ct);
        if (connectors.Count > 0)
        {
            prompt.AppendLine().AppendLine("Конектори (serverId → система):");
            foreach (var (id, name) in connectors)
            {
                prompt.Append(id).Append(" → ").AppendLine(name);
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        try
        {
            var reply = await chat.SendAsync(settings.Provider!, credential, settings.Model!, System, [LlmTurn.User(prompt.ToString())], [], deadline.Token, MaxTokens);
            if (string.IsNullOrWhiteSpace(reply.Text))
            {
                throw new LlmException("Модель повернула порожню відповідь.");
            }

            row.Text = reply.Text.Trim();
            row.Version = version;
            row.DefinitionHash = hash;
            row.Status = WorkflowExplanationStatus.Ready;
            row.Error = null;
        }
        catch (LlmException ex)
        {
            row.Status = WorkflowExplanationStatus.Failed;
            row.Error = ex.Message;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            row.Status = WorkflowExplanationStatus.Failed;
            row.Error = $"Модель не відповіла за {(int)Timeout.TotalSeconds} с.";
        }

        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return row.Status == WorkflowExplanationStatus.Ready ? ExplainOutcome.Ready : ExplainOutcome.Failed;
    }

    /// <summary>
    /// Значуща частина визначення: без <c>pinData</c>, нотаток і координат вузлів; довгі рядки обрізано. Від неї ж
    /// рахується відбиток, тож пересування вузлів пояснення не оновлює.
    /// </summary>
    /// <param name="document">Визначення.</param>
    /// <param name="name">Назва сценарію (джерело правди — <c>workflows.name</c>).</param>
    internal static JsonObject Essence(JsonObject document, string name)
    {
        var copy = document.DeepClone().AsObject();
        copy["name"] = name;
        copy.Remove("pinData");
        copy.Remove("notes");
        if (copy["nodes"] is JsonArray nodes)
        {
            foreach (var node in nodes.OfType<JsonObject>())
            {
                node.Remove("position");
            }
        }

        Trim(copy);
        return copy;
    }

    private static void Trim(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (property, value) in obj.ToList())
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > MaxStringChars)
                    {
                        obj[property] = s[..MaxStringChars] + "…";
                    }
                    else
                    {
                        Trim(value);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > MaxStringChars)
                    {
                        array[i] = s[..MaxStringChars] + "…";
                    }
                    else
                    {
                        Trim(array[i]);
                    }
                }

                break;
        }
    }

    private static async Task<List<(Guid Id, string Name)>> ConnectorNamesAsync(OpenBpDbContext db, JsonObject essence, CancellationToken ct)
    {
        var ids = (essence["nodes"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(n => n["parameters"]?["serverId"] is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var servers = await db.McpServers.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        return [.. servers.Select(s => (s.Id, s.Name))];
    }

    /// <summary>Системна інструкція.</summary>
    internal const string System =
        """
        You explain an OpenBP workflow (business process automation) to a business user who does not read code.
        Write in Ukrainian, in plain words, as plain text without Markdown symbols (no #, *, `, tables).

        The workflow is JSON: nodes [{ id, name, type, parameters, disabled? }] and connections [{ from: { node, output },
        to: { node, input } }], where node is a node id and output/input are port indexes. Node types: core.* are built-in
        steps (core.if / core.switch branch, core.set / core.code shape data, core.wait waits, core.merge joins branches,
        core.*Trigger and core.webhook / core.schedule start the process), mcp.tool calls an external system through a
        connector (serverId, toolName, arguments), mcp.poll checks an external system on a schedule, ai.* call an AI model.
        Expressions {{ … }} take data: $json — the current item, $node["Name"].json — data of an earlier step.

        Structure the answer exactly like this:
        Коротко — two or three sentences: what the process does, for whom, and what starts it.
        Кроки — a numbered list in the order the process runs (follow the connections, not the array order). For every
        enabled node: its name in «», what it does in business terms, which data it receives (from which step) and which
        data it passes on. For a branching step, describe each branch and its condition. Name external systems.
        Дані на вході і на виході — what the process receives at the start and what it produces or sends in the end.
        Помилки й очікування — waits, timeouts, error handling; omit this section if there is none.

        Describe only what the definition shows; if something is unclear (an empty parameter, a missing connector), say so
        briefly. Do not mention JSON, ids or type names unless there is no better word.
        """;
}

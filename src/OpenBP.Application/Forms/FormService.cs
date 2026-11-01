using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Forms;

/// <summary>
/// Реалізація <see cref="IFormService"/>. Тригер: <c>webhooks</c> (<c>kind = form</c>) → опублікована версія сценарію →
/// <c>auth</c> → перевірка за схемою → файли в <see cref="PostgresBinaryStore"/> → <see cref="IExecutionLauncher"/>.
/// Очікування (<c>resume/{token}</c>): токен <c>resume_tokens</c> (<c>kind = form</c>) → опис форми зі стану кроку
/// <c>waiting</c> → перевірка → <see cref="IWaitResumeService"/> з item'ом форми як сигналом.
/// </summary>
/// <param name="db">Контекст БД.</param>
/// <param name="launcher">Постановка виконання в чергу.</param>
/// <param name="resume">Пробудження виконання, що чекає.</param>
/// <param name="files">Сховище файлів форм.</param>
/// <param name="user">Користувач запиту (для <c>auth: login</c> і <c>submittedBy</c>).</param>
/// <param name="credentials">Джерело credentials для <c>auth: token</c>; береться ліниво.</param>
/// <param name="limits">Ліміти надсилання.</param>
/// <param name="time">Годинник для <c>submittedAt</c>; <c>null</c> — системний.</param>
public sealed class FormService(
    OpenBpDbContext db,
    IExecutionLauncher launcher,
    IWaitResumeService resume,
    PostgresBinaryStore files,
    ICurrentUser user,
    Func<ICredentialProvider> credentials,
    FormLimits limits,
    TimeProvider? time = null) : IFormService
{
    /// <summary>Префікс шляху форми очікування: <c>resume/{token}</c>.</summary>
    public const string ResumePrefix = "resume/";

    /// <summary>Типовий текст кнопки.</summary>
    public const string DefaultSubmitLabel = "Надіслати";

    private const string Entity = "form";
    private const string TriggerKind = "trigger";
    private const string WaitKind = "wait";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<FormDefinition> GetAsync(string path, string? token, CancellationToken ct)
    {
        var normalized = Webhooks.WebhookParameters.NormalizePath(path ?? "");
        if (ResumeToken(normalized) is { } resumeToken)
        {
            var wait = await FindWaitAsync(resumeToken, ct).ConfigureAwait(false);
            return Definition(WaitKind, wait.Form);
        }

        var trigger = await FindTriggerAsync(normalized, ct).ConfigureAwait(false);
        await AuthorizeAsync(trigger.Parameters, normalized, token, ct).ConfigureAwait(false);
        var p = trigger.Parameters;
        return new FormDefinition(TriggerKind, p.Title, p.Description, (JsonObject)p.Schema.DeepClone(), p.SubmitLabel ?? DefaultSubmitLabel);
    }

    /// <inheritdoc />
    public async Task<FormSubmitted> SubmitAsync(string path, FormSubmission submission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var normalized = Webhooks.WebhookParameters.NormalizePath(path ?? "");
        if (ResumeToken(normalized) is { } resumeToken)
        {
            var wait = await FindWaitAsync(resumeToken, ct).ConfigureAwait(false);
            var form = Definition(WaitKind, wait.Form);
            var (item, ids) = await PrepareAsync(form.Schema, submission, ct).ConfigureAwait(false);
            var signal = new JsonObject
            {
                ["json"] = item.Json.DeepClone(),
                ["binary"] = item.Binary is { Count: > 0 } binary ? JsonSerializer.SerializeToNode(binary, JsonSerializerOptions.Web) : null,
            };
            try
            {
                await resume.ResumeAsync(resumeToken, signal, ResumeTokens.FormKind, ct).ConfigureAwait(false);
            }
            catch
            {
                await files.DeleteUnattachedAsync(ids, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await files.AttachAsync(ids, wait.ExecutionId, ct).ConfigureAwait(false);
            return new FormSubmitted(wait.ExecutionId, Text(wait.Form, "message"), null);
        }

        var trigger = await FindTriggerAsync(normalized, ct).ConfigureAwait(false);
        await AuthorizeAsync(trigger.Parameters, normalized, submission.Token, ct).ConfigureAwait(false);
        var (startItem, fileIds) = await PrepareAsync(trigger.Parameters.Schema, submission, ct).ConfigureAwait(false);
        Guid executionId;
        try
        {
            executionId = (await launcher.LaunchAsync(
                new ExecutionLaunch(trigger.WorkflowId, ExecutionMode.Trigger, trigger.NodeId, [startItem]), ct).ConfigureAwait(false)).Id;
        }
        catch (Exception ex)
        {
            await files.DeleteUnattachedAsync(fileIds, CancellationToken.None).ConfigureAwait(false);
            if (ex is ConflictException { Code: ExecutionLauncher.WorkflowInactiveCode })
            {
                // Сценарій деактивовано між пошуком і запуском.
                throw new NotFoundException(Entity, normalized);
            }

            throw;
        }

        await files.AttachAsync(fileIds, executionId, ct).ConfigureAwait(false);
        return new FormSubmitted(executionId, trigger.Parameters.Message, trigger.Parameters.RedirectUrl);
    }

    /// <summary>Токен з шляху <c>resume/{token}</c> або <c>null</c>.</summary>
    private static string? ResumeToken(string path) =>
        path.StartsWith(ResumePrefix, StringComparison.OrdinalIgnoreCase) && path.Length > ResumePrefix.Length && !path[ResumePrefix.Length..].Contains('/', StringComparison.Ordinal)
            ? path[ResumePrefix.Length..]
            : null;

    private static FormDefinition Definition(string kind, JsonObject form) => new(
        kind,
        Text(form, "title") ?? "Форма",
        Text(form, "description"),
        form["schema"] is JsonObject schema ? (JsonObject)schema.DeepClone() : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        Text(form, "submitLabel") ?? DefaultSubmitLabel);

    /// <summary>
    /// Поля за схемою (порожні відкинуто, у полях-файлах — назва файлу), ліміт кількості файлів, збереження файлів
    /// (ще не прив'язаних до виконання) і item <c>{ fields, submittedAt, submittedBy? }</c> з <c>binary</c>.
    /// </summary>
    private async Task<(Item Item, List<string> FileIds)> PrepareAsync(JsonObject schema, FormSubmission submission, CancellationToken ct)
    {
        if (submission.Files.Count > limits.MaxFiles)
        {
            throw new FormValidationException([new("", $"Можна додати не більше {limits.MaxFiles} файлів.")]);
        }

        var fileFields = FormSchema.FileFields(schema);
        var fields = FormSchema.WithoutEmpty(submission.Fields);
        var errors = new List<FormFieldError>();
        foreach (var name in fileFields)
        {
            // Назву файлу в полі задає лише сам файл.
            fields.Remove(name);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in submission.Files)
        {
            if (!fileFields.Contains(file.Field))
            {
                errors.Add(new(file.Field, "Це поле не приймає файлів."));
            }
            else if (!seen.Add(file.Field))
            {
                errors.Add(new(file.Field, "У це поле можна додати лише один файл."));
            }
            else
            {
                fields[file.Field] = file.FileName;
            }
        }

        // Приховані умовами «Показувати, якщо» поля (F11-06) не зберігаються, разом з їхніми файлами.
        var hidden = VisibleIf.HiddenFields(schema, fields);
        fields = VisibleIf.WithoutHidden(schema, fields);
        errors.AddRange(FormSchema.Validate(schema, fields).Where(e => errors.All(x => x.Field != e.Field)));
        if (errors.Count > 0)
        {
            throw new FormValidationException(errors);
        }

        var binary = new Dictionary<string, BinaryRef>(StringComparer.Ordinal);
        var ids = new List<string>();
        try
        {
            foreach (var file in submission.Files.Where(f => !hidden.Contains(f.Field)))
            {
                var reference = await files.PutAsync(file.Content, string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType, file.FileName, ct)
                    .ConfigureAwait(false);
                ids.Add(reference.Id);
                binary[file.Field] = reference;
            }
        }
        catch
        {
            await files.DeleteUnattachedAsync(ids, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var json = new JsonObject
        {
            ["fields"] = fields,
            ["submittedAt"] = _time.GetUtcNow(),
        };
        if (user is { IsAuthenticated: true, UserId: { } id })
        {
            json["submittedBy"] = new JsonObject { ["id"] = id.ToString(), ["email"] = user.Email };
        }

        return (new Item { Json = json, Binary = binary.Count > 0 ? binary : null }, ids);
    }

    private sealed record Trigger(Guid WorkflowId, string NodeId, FormTriggerParameters Parameters);

    private async Task<Trigger> FindTriggerAsync(string path, CancellationToken ct)
    {
        var hook = await db.Webhooks.AsNoTracking()
                       .SingleOrDefaultAsync(w => w.Kind == WebhookEntity.FormKind && w.Path == path, ct).ConfigureAwait(false)
                   ?? throw new NotFoundException(Entity, path);
        var definition = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == hook.WorkflowId && w.Active)
            .Join(db.WorkflowVersions, w => w.PublishedVersionId ?? w.CurrentVersionId, v => v.Id, (w, v) => v.Definition)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new NotFoundException(Entity, path);
        var node = WorkflowJson.Deserialize(definition).Nodes
                       .SingleOrDefault(n => n.Id == hook.NodeId && n.Type == FormTriggerParameters.NodeType)
                   ?? throw new NotFoundException(Entity, path);
        return new Trigger(hook.WorkflowId, hook.NodeId, FormTriggerParameters.Read(node));
    }

    private sealed record Wait(Guid ExecutionId, JsonObject Form);

    /// <summary>Токен форми, що чекає надсилання: виконання в стані <c>waiting</c> і крок вузла <c>waiting</c> з описом форми.</summary>
    private async Task<Wait> FindWaitAsync(string token, CancellationToken ct)
    {
        var row = await db.ResumeTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Token == token, ct).ConfigureAwait(false);
        if (row is null || row.Kind != ResumeTokens.FormKind)
        {
            throw new NotFoundException(Entity, ResumePrefix + "…");
        }

        if (row.UsedAt is not null)
        {
            throw new ConflictException(WaitResumeService.TokenUsedCode, "Цю форму вже надіслано: виконання продовжилось.");
        }

        var status = await db.Executions.AsNoTracking().Where(e => e.Id == row.ExecutionId).Select(e => e.Status).SingleOrDefaultAsync(ct).ConfigureAwait(false)
                     ?? throw new NotFoundException(Entity, ResumePrefix + "…");
        var step = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ExecutionId == row.ExecutionId && s.NodeId == row.NodeId)
            .OrderByDescending(s => s.Id)
            .Select(s => new { s.Status, s.WaitState })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (status != ExecutionStatuses.Waiting || step?.Status != ExecutionStatuses.Waiting)
        {
            throw new ConflictException(status == ExecutionStatuses.Waiting || status is ExecutionStatuses.Queued or ExecutionStatuses.Running
                    ? WaitResumeService.NodeNotWaitingCode
                    : WaitResumeService.ExecutionNotWaitingCode,
                status is ExecutionStatuses.Queued or ExecutionStatuses.Running || step is null
                    ? "Форма ще не готова: процес не дійшов до цього кроку. Спробуйте трохи згодом."
                    : "Ця форма вже неактуальна: процес завершено або строк відповіді вийшов.");
        }

        var state = step.WaitState is { } raw ? JsonNode.Parse(raw) as JsonObject : null;
        return new Wait(row.ExecutionId, state?["form"] as JsonObject ?? []);
    }

    /// <summary><c>auth</c> тригера: <c>login</c> — сесія; <c>token</c> — токен з посилання збігається з credential'ом.</summary>
    private async Task AuthorizeAsync(FormTriggerParameters parameters, string path, string? token, CancellationToken ct)
    {
        switch (parameters.Auth)
        {
            case FormAuth.Login when !user.IsAuthenticated:
                throw new FormLoginRequiredException("/login?returnUrl=" + Uri.EscapeDataString("/form/" + path));
            case FormAuth.Token:
                if (string.IsNullOrEmpty(token) || parameters.CredentialId is not { } credentialId)
                {
                    throw new UnauthenticatedException();
                }

                JsonObject secret;
                try
                {
                    secret = await credentials().GetAsync(credentialId, ct).ConfigureAwait(false);
                }
                catch (CredentialNotFoundException)
                {
                    // Немає credential'а — форма закрита: краще відмовити, ніж пустити без перевірки.
                    throw new UnauthenticatedException();
                }

                if (!Equal(token, Text(secret, "token")))
                {
                    throw new UnauthenticatedException();
                }

                break;
        }
    }

    private static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    /// <summary>Порівняння за сталий час; порожній очікуваний секрет ніколи не збігається.</summary>
    private static bool Equal(string actual, string? expected) =>
        expected is not null
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

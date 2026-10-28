using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Application.Assistant;

/// <summary>
/// Де користувач зараз (F3-108): лише ідентифікатори й назви, решту помічник бере інструментами. Усі поля необов'язкові.
/// </summary>
/// <param name="Page">Сторінка застосунку, напр. <c>/workflows/…</c>.</param>
/// <param name="WorkflowId">Відкритий сценарій.</param>
/// <param name="NodeId">Вибраний вузол сценарію.</param>
/// <param name="ExecutionId">Відкрите виконання.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="Table">Відкрита таблиця (id або назва).</param>
public sealed record AssistantPageContext(
    string? Page = null,
    Guid? WorkflowId = null,
    string? NodeId = null,
    Guid? ExecutionId = null,
    Guid? ProjectId = null,
    string? Table = null);

/// <summary>Тіло <c>POST /assistant/conversations/{id}/messages</c>.</summary>
/// <param name="Message">Запит користувача.</param>
/// <param name="Context">Де користувач зараз.</param>
public sealed record AssistantChatRequest(string? Message, AssistantPageContext? Context = null);

/// <summary>Тіло <c>POST /assistant/conversations</c>.</summary>
/// <param name="Title">Назва; <c>null</c> — з першого повідомлення.</param>
public sealed record AssistantConversationInput(string? Title = null);

/// <summary>Рішення щодо пропозиції змін.</summary>
/// <param name="Apply"><c>true</c> — застосувати, <c>false</c> — скасувати.</param>
public sealed record AssistantDecision(bool Apply);

/// <summary>Розмова в списку.</summary>
/// <param name="Id">Id.</param>
/// <param name="Title">Назва.</param>
/// <param name="UserId">Автор.</param>
/// <param name="MessageCount">Скільки реплік людини й помічника.</param>
/// <param name="CreatedAt">Створено.</param>
/// <param name="UpdatedAt">Остання репліка.</param>
public sealed record AssistantConversationSummary(Guid Id, string Title, Guid UserId, int MessageCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Репліка розмови для показу.</summary>
/// <param name="Role"><c>user</c>, <c>assistant</c>, <c>tool</c> (крок інструмента) або <c>note</c> (рішення щодо пропозиції).</param>
/// <param name="Text">Текст; для <c>tool</c> — короткий підсумок.</param>
/// <param name="Tool">Інструмент кроку.</param>
/// <param name="Ok">Крок без помилки.</param>
/// <param name="At">Коли.</param>
/// <param name="ProposalId">Для кроку <c>tool</c>, що завершився пропозицією змін, — її id (картка в чаті, F3-111).</param>
public sealed record AssistantMessageView(string Role, string? Text, string? Tool, bool? Ok, DateTimeOffset At, Guid? ProposalId = null);

/// <summary>
/// Пропозиція змін (F3-108): інструмент, що змінює дані, помічник не виконує — людина застосовує чи скасовує її.
/// </summary>
/// <param name="Id">Id.</param>
/// <param name="Tool">Інструмент.</param>
/// <param name="Arguments">Аргументи, з якими його буде викликано.</param>
/// <param name="Plan">План від інструмента в режимі «пропозиція» (<c>create_table</c>, <c>alter_table</c>), інакше <c>null</c>.</param>
/// <param name="Status"><c>pending</c>, <c>applied</c>, <c>rejected</c> або <c>failed</c>.</param>
/// <param name="Result">Результат застосування (JSON-текст) або помилка.</param>
/// <param name="CreatedAt">Створено.</param>
/// <param name="DecidedAt">Вирішено.</param>
public sealed record AssistantProposalView(
    Guid Id,
    string Tool,
    JsonObject Arguments,
    JsonNode? Plan,
    string Status,
    string? Result,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);

/// <summary>Розмова повністю.</summary>
/// <param name="Id">Id.</param>
/// <param name="Title">Назва.</param>
/// <param name="UserId">Автор.</param>
/// <param name="Messages">Репліки в порядку часу.</param>
/// <param name="Proposals">Пропозиції змін.</param>
/// <param name="CreatedAt">Створено.</param>
/// <param name="UpdatedAt">Остання репліка.</param>
public sealed record AssistantConversation(
    Guid Id,
    string Title,
    Guid UserId,
    IReadOnlyList<AssistantMessageView> Messages,
    IReadOnlyList<AssistantProposalView> Proposals,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Подія відповіді загального помічника (SSE): <c>step</c> — інструмент (<see cref="Tool"/>, <see cref="Ok"/>, підсумок у
/// <see cref="Text"/>), <c>proposal</c> — пропозиція змін, <c>message</c> — текст моделі, <c>error</c> — збій
/// (<see cref="Code"/>), <c>done</c> — кінець.
/// </summary>
/// <param name="Type">Вид події.</param>
/// <param name="Text">Текст.</param>
/// <param name="Tool">Інструмент.</param>
/// <param name="Ok">Інструмент без помилки.</param>
/// <param name="Proposal">Пропозиція.</param>
/// <param name="Code">Код помилки.</param>
public sealed record AssistantChatEvent(
    string Type,
    string? Text = null,
    string? Tool = null,
    bool? Ok = null,
    AssistantProposalView? Proposal = null,
    string? Code = null);

/// <summary>Розмова, підготовлена <see cref="IAssistantSessionService.SendAsync"/>.</summary>
public interface IAssistantChat
{
    /// <summary>Виконує запит: події до <c>done</c> включно; розмова зберігається наприкінці.</summary>
    /// <param name="ct">Скасування (клієнт закрив з'єднання).</param>
    IAsyncEnumerable<AssistantChatEvent> RunAsync(CancellationToken ct);
}

/// <summary>
/// Загальний помічник (F3-108, docs/global-assistant.md): розмови з пам'яттю на кожній сторінці. Інструменти — tools MCP-сервера
/// OpenBP (<see cref="IAssistantToolbox"/>) з правами користувача: ті, що читають, виконуються одразу; ті, що змінюють, стають
/// пропозиціями й виконуються лише після рішення людини (<see cref="DecideAsync"/>). Лише editor і admin; розмову бачать
/// її автор і admin, пише в неї лише автор.
/// </summary>
public interface IAssistantSessionService
{
    /// <summary>Мої розмови, новіші першими; admin з <paramref name="userId"/> — розмови іншого користувача.</summary>
    Task<IReadOnlyList<AssistantConversationSummary>> ListAsync(Guid? userId, CancellationToken ct);

    /// <summary>Розмова повністю.</summary>
    /// <exception cref="NotFoundException">Немає або чужа (не для admin).</exception>
    Task<AssistantConversation> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Нова порожня розмова.</summary>
    Task<AssistantConversation> CreateAsync(AssistantConversationInput input, CancellationToken ct);

    /// <summary>Видаляє розмову (автор або admin).</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Перевіряє права, налаштування й запит і готує відповідь помічника.</summary>
    /// <exception cref="ArgumentException">Порожній чи задовгий запит.</exception>
    /// <exception cref="AssistantNotConfiguredException">Помічник не налаштований.</exception>
    Task<IAssistantChat> SendAsync(Guid id, AssistantChatRequest request, CancellationToken ct);

    /// <summary>Застосовує чи скасовує пропозицію змін; результат стає приміткою для наступної репліки моделі.</summary>
    /// <exception cref="ConflictException">Пропозицію вже вирішено (<c>ASSISTANT_PROPOSAL_DECIDED</c>).</exception>
    Task<AssistantProposalView> DecideAsync(Guid id, Guid proposalId, AssistantDecision decision, CancellationToken ct);
}

/// <summary>Admin читає чужу розмову, але писати в неї й вирішувати пропозиції може лише автор (403 <c>FORBIDDEN</c>).</summary>
public sealed class NotAuthorOfConversationException()
    : OpenBpException("FORBIDDEN", 403, "Писати в розмову й вирішувати її пропозиції може лише її автор.");

/// <summary>Ліміти розмов загального помічника.</summary>
/// <param name="MaxMessageChars">Найдовше повідомлення людини.</param>
/// <param name="MaxStoredTurns">Скільки реплік зберігається; старіші відкидаються.</param>
/// <param name="MaxContextTurns">Скільки останніх реплік іде моделі.</param>
/// <param name="MaxToolResultChars">Найдовший результат інструмента для моделі.</param>
public sealed record AssistantConversationLimits(int MaxMessageChars, int MaxStoredTurns, int MaxContextTurns, int MaxToolResultChars)
{
    /// <summary>8000 символів, 300 реплік, 40 у контексті, 12 000 символів результату.</summary>
    public static AssistantConversationLimits Default { get; } = new(8000, 300, 40, 12_000);
}

/// <summary>Реалізація <see cref="IAssistantSessionService"/>.</summary>
public sealed class AssistantSessionService(
    ICurrentUser currentUser,
    IAssistantSettingsService settings,
    IAssistantToolbox toolbox,
    IDbContextFactory<OpenBpDbContext> contexts,
    CredentialEncryptionKey key,
    LlmChat chat,
    TimeProvider time,
    AssistantLimits? limits = null,
    AssistantConversationLimits? conversationLimits = null) : IAssistantSessionService
{
    /// <summary>Код конфлікту: пропозицію вже застосовано чи скасовано.</summary>
    public const string ProposalDecidedCode = "ASSISTANT_PROPOSAL_DECIDED";

    private const string Entity = "assistant-conversation";
    private const int TitleLength = 80;
    private const int StoredResultChars = 4000;

    private static readonly JsonSerializerOptions StoreJson = new(JsonSerializerDefaults.Web);

    private readonly ICurrentUser currentUser = currentUser;
    private readonly IAssistantToolbox toolbox = toolbox;
    private readonly IDbContextFactory<OpenBpDbContext> contexts = contexts;
    private readonly LlmChat chat = chat;
    private readonly TimeProvider time = time;
    private readonly AssistantLimits limits = limits ?? AssistantLimits.Default;
    private readonly AssistantConversationLimits conversationLimits = conversationLimits ?? AssistantConversationLimits.Default;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantConversationSummary>> ListAsync(Guid? userId, CancellationToken ct)
    {
        var me = RequireUser();
        var owner = userId ?? me;
        if (owner != me && currentUser.Role != UserRole.Admin)
        {
            throw new ForbiddenException(UserRole.Admin);
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.AssistantConversations.AsNoTracking().Where(c => c.UserId == owner).OrderByDescending(c => c.UpdatedAt).ToListAsync(ct);
        return [.. rows.Select(r => new AssistantConversationSummary(
            r.Id, r.Title, r.UserId, Parse(r.Data).Turns.Count(t => t.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(t.Text)), r.CreatedAt, r.UpdatedAt))];
    }

    /// <inheritdoc />
    public async Task<AssistantConversation> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return ToView(await LoadAsync(db, id, write: false, ct));
    }

    /// <inheritdoc />
    public async Task<AssistantConversation> CreateAsync(AssistantConversationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var me = RequireUser();
        var now = time.GetUtcNow();
        var entity = new AssistantConversationEntity
        {
            Id = Guid.CreateVersion7(now),
            UserId = me,
            Title = Title(input.Title) ?? "",
            Data = Serialize(new StoredConversation()),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.AssistantConversations.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToView(entity);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entity = await LoadAsync(db, id, write: false, ct);
        db.AssistantConversations.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IAssistantChat> SendAsync(Guid id, AssistantChatRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireUser();
        var message = request.Message?.Trim();
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("Напишіть, з чим допомогти.", nameof(request));
        }

        if (message.Length > conversationLimits.MaxMessageChars)
        {
            throw new ArgumentException($"Повідомлення довше за {conversationLimits.MaxMessageChars} символів — скоротіть його.", nameof(request));
        }

        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            await LoadAsync(db, id, write: true, ct);
        }

        var current = await settings.GetAsync(ct);
        if (!current.Configured)
        {
            throw new AssistantNotConfiguredException();
        }

        var credential = await new CredentialStore(contexts, key).GetDataAsync(current.CredentialId!.Value, ct)
            ?? throw new AssistantNotConfiguredException();
        return new Chat(this, id, message, request.Context, current.Provider!, current.Model!, credential);
    }

    /// <inheritdoc />
    public async Task<AssistantProposalView> DecideAsync(Guid id, Guid proposalId, AssistantDecision decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entity = await LoadAsync(db, id, write: true, ct);
        var stored = Parse(entity.Data);
        var proposal = stored.Proposals.FirstOrDefault(p => p.Id == proposalId) ?? throw new NotFoundException("assistant-proposal", proposalId);
        if (proposal.Status != ProposalStatus.Pending)
        {
            throw new ConflictException(ProposalDecidedCode, "Цю пропозицію вже вирішено.");
        }

        var now = time.GetUtcNow();
        string note;
        if (!decision.Apply)
        {
            proposal.Status = ProposalStatus.Rejected;
            note = $"Користувач скасував пропозицію «{proposal.Tool}» — її не виконано.";
        }
        else
        {
            var arguments = (JsonObject)proposal.Arguments.DeepClone();
            if (proposal.Plan?["planId"] is JsonValue planId)
            {
                arguments["planId"] = planId.DeepClone();
            }

            await using var tools = await toolbox.OpenAsync(ct);
            var result = await tools.CallAsync(proposal.Tool, arguments, ct);
            proposal.Status = result.IsError ? ProposalStatus.Failed : ProposalStatus.Applied;
            proposal.Result = Truncate(result.Text, StoredResultChars);
            note = result.IsError
                ? $"Користувач застосував пропозицію «{proposal.Tool}», але вона не вдалася: {Truncate(result.Text, 500)}"
                : $"Користувач застосував пропозицію «{proposal.Tool}». Результат: {Truncate(result.Text, 1500)}";
        }

        proposal.DecidedAt = now;
        stored.Turns.Add(new StoredTurn("note", note, At: now));
        entity.Data = Serialize(stored);
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ToView(proposal);
    }

    private Guid RequireUser()
    {
        currentUser.Require(UserRole.Editor);
        return currentUser.UserId ?? throw new ForbiddenException(UserRole.Editor);
    }

    /// <summary>Розмова для читання — автору й admin; для запису — лише автору. Чужа для інших — 404.</summary>
    private async Task<AssistantConversationEntity> LoadAsync(OpenBpDbContext db, Guid id, bool write, CancellationToken ct)
    {
        var me = RequireUser();
        var entity = await db.AssistantConversations.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null || (entity.UserId != me && currentUser.Role != UserRole.Admin))
        {
            throw new NotFoundException(Entity, id);
        }

        if (write && entity.UserId != me)
        {
            throw new NotAuthorOfConversationException();
        }

        return entity;
    }

    private async Task SaveTurnsAsync(Guid id, string message, IReadOnlyList<StoredTurn> turns, IReadOnlyList<StoredProposal> proposals, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entity = await db.AssistantConversations.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null)
        {
            return;
        }

        var stored = Parse(entity.Data);
        stored.Turns.AddRange(turns);
        stored.Proposals.AddRange(proposals);
        Trim(stored.Turns, conversationLimits.MaxStoredTurns);
        entity.Data = Serialize(stored);
        if (entity.Title.Length == 0)
        {
            entity.Title = Title(message) ?? "";
        }

        entity.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Лишає не більше <paramref name="max"/> реплік, починаючи з репліки людини.</summary>
    private static void Trim(List<StoredTurn> turns, int max)
    {
        if (turns.Count <= max)
        {
            return;
        }

        var start = turns.Count - max;
        while (start < turns.Count && turns[start].Role != "user")
        {
            start++;
        }

        turns.RemoveRange(0, start);
    }

    private static string? Title(string? text)
    {
        var line = text?.Split('\n', 2)[0].Trim();
        return string.IsNullOrEmpty(line) ? null : line.Length <= TitleLength ? line : line[..TitleLength].TrimEnd() + "…";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…(обрізано)";

    private static StoredConversation Parse(string data) =>
        JsonSerializer.Deserialize<StoredConversation>(data, StoreJson) ?? new StoredConversation();

    private static string Serialize(StoredConversation conversation) => JsonSerializer.Serialize(conversation, StoreJson);

    private static AssistantConversation ToView(AssistantConversationEntity entity)
    {
        var stored = Parse(entity.Data);
        var messages = stored.Turns
            .Where(t => t.Role is "tool" or "note" || !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => new AssistantMessageView(
                t.Role,
                t.Role == "tool" ? t.Summary : t.Text,
                t.Tool,
                t.Ok,
                t.At,
                t.Role == "tool" && t.CallId is not null ? stored.Proposals.FirstOrDefault(p => p.CallId == t.CallId)?.Id : null))
            .ToList();
        return new AssistantConversation(entity.Id, entity.Title, entity.UserId, messages, [.. stored.Proposals.Select(ToView)], entity.CreatedAt, entity.UpdatedAt);
    }

    private static AssistantProposalView ToView(StoredProposal p) =>
        new(p.Id, p.Tool, (JsonObject)p.Arguments.DeepClone(), p.Plan?.DeepClone(), p.Status, p.Result, p.CreatedAt, p.DecidedAt);

    /// <summary>
    /// Репліки для моделі: останні <see cref="AssistantConversationLimits.MaxContextTurns"/>, починаючи з репліки людини;
    /// контекст сторінки й примітки (рішення щодо пропозицій) — рядками перед текстом людини; виклик без результату
    /// (запит обірвався) отримує результат «не виконано».
    /// </summary>
    internal static List<LlmTurn> ToModelTurns(IReadOnlyList<StoredTurn> stored, int maxTurns)
    {
        var start = Math.Max(0, stored.Count - maxTurns);
        while (start < stored.Count && stored[start].Role != "user")
        {
            start++;
        }

        var turns = new List<LlmTurn>();
        var notes = new List<string>();
        var pending = new List<string>();

        void ClosePending()
        {
            foreach (var callId in pending)
            {
                turns.Add(LlmTurn.Tool(callId, "{\"error\":\"not_executed\"}"));
            }

            pending.Clear();
        }

        foreach (var turn in stored.Skip(start))
        {
            switch (turn.Role)
            {
                case "tool" when turn.CallId is not null && pending.Remove(turn.CallId):
                    turns.Add(LlmTurn.Tool(turn.CallId, turn.Text ?? ""));
                    break;
                case "note":
                    notes.Add(turn.Text ?? "");
                    break;
                case "user":
                    ClosePending();
                    var text = new StringBuilder();
                    foreach (var note in notes)
                    {
                        text.Append("[Примітка: ").Append(note).Append("]\n");
                    }

                    notes.Clear();
                    if (ContextLine(turn.Context) is { } context)
                    {
                        text.Append(context).Append('\n');
                    }

                    turns.Add(LlmTurn.User(text.Append(turn.Text).ToString()));
                    break;
                case "assistant":
                    ClosePending();
                    var calls = turn.Calls?.Select(c => new LlmToolCall(c.Id, c.Name, (JsonObject)c.Arguments.DeepClone())).ToList();
                    turns.Add(new LlmTurn("assistant", turn.Text, calls is { Count: > 0 } ? calls : null));
                    pending.AddRange(calls?.Select(c => c.Id) ?? []);
                    break;
            }
        }

        ClosePending();
        return turns;
    }

    /// <summary>Рядок контексту сторінки для моделі або <c>null</c>.</summary>
    internal static string? ContextLine(AssistantPageContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var parts = new List<string>();
        void Add(string name, object? value)
        {
            if (value is not null && (value is not string s || !string.IsNullOrWhiteSpace(s)))
            {
                parts.Add($"{name}: {value}");
            }
        }

        Add("сторінка", context.Page?.Trim());
        Add("сценарій", context.WorkflowId);
        Add("вузол", context.NodeId?.Trim());
        Add("виконання", context.ExecutionId);
        Add("проєкт", context.ProjectId);
        Add("таблиця", context.Table?.Trim());
        return parts.Count == 0 ? null : $"[Контекст: {string.Join("; ", parts)}]";
    }

    private sealed class Chat(
        AssistantSessionService owner,
        Guid conversationId,
        string message,
        AssistantPageContext? context,
        string provider,
        string model,
        JsonObject credential) : IAssistantChat
    {
        private readonly List<StoredTurn> added = [];
        private readonly List<StoredProposal> proposals = [];

        public async IAsyncEnumerable<AssistantChatEvent> RunAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var limits = owner.limits;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(limits.Timeout);
            List<StoredTurn> history;
            await using (var db = await owner.contexts.CreateDbContextAsync(ct))
            {
                history = Parse((await owner.LoadAsync(db, conversationId, write: true, ct)).Data).Turns;
            }

            added.Add(new StoredTurn("user", message, At: owner.time.GetUtcNow(), Context: context));
            IAssistantToolSession? tools = null;
            AssistantChatEvent? failure = null;
            try
            {
                try
                {
                    tools = await owner.toolbox.OpenAsync(deadline.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    failure = new AssistantChatEvent("error", "Інструменти помічника недоступні: " + ex.Message, Code: "ASSISTANT_TOOLS_UNAVAILABLE");
                }

                if (failure is not null)
                {
                    yield return failure;
                }
                else
                {
                    await foreach (var e in LoopAsync(tools!, history, deadline.Token, ct))
                    {
                        yield return e;
                    }
                }
            }
            finally
            {
                if (tools is not null)
                {
                    await tools.DisposeAsync();
                }

                // Розмова зберігається й тоді, коли клієнт закрив з'єднання.
                await owner.SaveTurnsAsync(conversationId, message, added, proposals, CancellationToken.None);
            }

            yield return new AssistantChatEvent("done");
        }

        private async IAsyncEnumerable<AssistantChatEvent> LoopAsync(
            IAssistantToolSession tools, List<StoredTurn> history, CancellationToken deadline, [EnumeratorCancellation] CancellationToken ct)
        {
            var limits = owner.limits;
            var llmTools = tools.Tools.Select(t => new LlmTool(t.Name, t.Description, t.InputSchema)).ToList();
            var steps = 0;
            while (true)
            {
                var turns = ToModelTurns([.. history, .. added], owner.conversationLimits.MaxContextTurns);
                LlmReply reply;
                AssistantChatEvent? failure = null;
                try
                {
                    reply = await owner.chat.SendAsync(provider, credential, model, GlobalAssistantPrompt.System, turns, llmTools, deadline);
                }
                catch (LlmException ex)
                {
                    reply = new LlmReply("", []);
                    failure = new AssistantChatEvent("error", ex.Message, Code: "ASSISTANT_PROVIDER_ERROR");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    reply = new LlmReply("", []);
                    failure = TimedOut(limits);
                }

                if (failure is not null)
                {
                    yield return failure;
                    yield break;
                }

                var now = owner.time.GetUtcNow();
                added.Add(new StoredTurn(
                    "assistant",
                    reply.Text,
                    [.. reply.ToolCalls.Select(c => new StoredCall(c.Id, c.Name, c.Arguments))],
                    At: now));
                if (!string.IsNullOrWhiteSpace(reply.Text))
                {
                    yield return new AssistantChatEvent("message", reply.Text);
                }

                if (reply.ToolCalls.Count == 0)
                {
                    yield break;
                }

                if (steps + reply.ToolCalls.Count > limits.MaxToolSteps)
                {
                    yield return new AssistantChatEvent(
                        "error",
                        $"Помічник зробив {limits.MaxToolSteps} кроків інструментів і зупинився. Уточніть запит або розбийте його на частини.",
                        Code: "ASSISTANT_STEP_LIMIT");
                    yield break;
                }

                foreach (var call in reply.ToolCalls)
                {
                    steps++;
                    Outcome outcome;
                    try
                    {
                        outcome = await ExecuteAsync(tools, call, deadline);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        failure = TimedOut(limits);
                        break;
                    }

                    added.Add(new StoredTurn(
                        "tool",
                        Truncate(outcome.Result, owner.conversationLimits.MaxToolResultChars),
                        CallId: call.Id,
                        Tool: call.Name,
                        Ok: outcome.Ok,
                        Summary: outcome.Summary,
                        At: owner.time.GetUtcNow()));
                    yield return new AssistantChatEvent("step", outcome.Summary, call.Name, outcome.Ok);
                    if (outcome.Proposal is { } proposal)
                    {
                        proposals.Add(proposal);
                        yield return new AssistantChatEvent("proposal", Tool: call.Name, Proposal: ToView(proposal));
                    }
                }

                if (failure is not null)
                {
                    yield return failure;
                    yield break;
                }
            }
        }

        private static AssistantChatEvent TimedOut(AssistantLimits limits) => new(
            "error",
            $"Помічник не вклався в {(int)limits.Timeout.TotalSeconds} с. Уточніть запит або повторіть.",
            Code: "ASSISTANT_TIMEOUT");

        /// <summary>
        /// Читання — одразу; інструмент з планом — план і пропозиція; решта змін — лише пропозиція, без виконання.
        /// </summary>
        private async Task<Outcome> ExecuteAsync(IAssistantToolSession tools, LlmToolCall call, CancellationToken ct)
        {
            var tool = tools.Tools.FirstOrDefault(t => t.Name == call.Name);
            if (tool is null)
            {
                return Failed($"Невідомий інструмент «{call.Name}».", "unknown_tool");
            }

            if (tool.ReadOnly)
            {
                var result = await tools.CallAsync(call.Name, call.Arguments, ct);
                return new Outcome(!result.IsError, Summary(result), result.Text, null);
            }

            var arguments = (JsonObject)call.Arguments.DeepClone();
            JsonNode? plan = null;
            if (tool.PlansChanges)
            {
                arguments.Remove("planId");
                var planned = await tools.CallAsync(call.Name, arguments, ct);
                if (planned.IsError)
                {
                    return new Outcome(false, Summary(planned), planned.Text, null);
                }

                plan = planned.Content?["plan"]?.DeepClone();
            }

            var proposal = new StoredProposal
            {
                Id = Guid.CreateVersion7(owner.time.GetUtcNow()),
                CallId = call.Id,
                Tool = call.Name,
                Arguments = arguments,
                Plan = plan,
                CreatedAt = owner.time.GetUtcNow(),
            };
            var answer = new JsonObject
            {
                ["status"] = "awaiting_confirmation",
                ["proposalId"] = proposal.Id.ToString(),
                ["message"] = "Nothing is changed yet: the user sees a card with Apply / Cancel. Do not repeat this call; " +
                              "briefly tell the user what will happen. Their decision arrives as a note with the next message.",
                ["plan"] = plan?.DeepClone(),
            };
            return new Outcome(true, plan is null ? "пропозиція змін — чекає рішення" : "план змін — чекає рішення", answer.ToJsonString(), proposal);
        }

        private static string Summary(AssistantToolResult result)
        {
            if (!result.IsError)
            {
                return "виконано";
            }

            var text = result.Content?["message"]?.GetValueKind() == JsonValueKind.String ? (string)result.Content["message"]! : result.Text;
            return Truncate(text, 300);
        }

        private static Outcome Failed(string message, string code) =>
            new(false, message, new JsonObject { ["error"] = code, ["message"] = message }.ToJsonString(), null);
    }

    private sealed record Outcome(bool Ok, string Summary, string Result, StoredProposal? Proposal);

    /// <summary>Збережена розмова: <c>data</c> у <c>assistant_conversations</c>.</summary>
    internal sealed class StoredConversation
    {
        public List<StoredTurn> Turns { get; set; } = [];

        public List<StoredProposal> Proposals { get; set; } = [];
    }

    /// <summary>Збережена репліка: <c>user</c>, <c>assistant</c>, <c>tool</c> або <c>note</c>.</summary>
    internal sealed record StoredTurn(
        string Role,
        string? Text,
        List<StoredCall>? Calls = null,
        string? CallId = null,
        string? Tool = null,
        bool? Ok = null,
        string? Summary = null,
        DateTimeOffset At = default,
        AssistantPageContext? Context = null);

    /// <summary>Збережений виклик інструмента.</summary>
    internal sealed record StoredCall(string Id, string Name, JsonObject Arguments);

    /// <summary>Збережена пропозиція змін.</summary>
    internal sealed class StoredProposal
    {
        public Guid Id { get; set; }

        public string CallId { get; set; } = "";

        public string Tool { get; set; } = "";

        public JsonObject Arguments { get; set; } = new();

        public JsonNode? Plan { get; set; }

        public string Status { get; set; } = ProposalStatus.Pending;

        public string? Result { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset? DecidedAt { get; set; }
    }

    private static class ProposalStatus
    {
        public const string Pending = "pending";
        public const string Applied = "applied";
        public const string Rejected = "rejected";
        public const string Failed = "failed";
    }
}

/// <summary>Системна інструкція загального помічника.</summary>
internal static class GlobalAssistantPrompt
{
    public const string System =
        """
        You are the OpenBP assistant: a side panel available on every page of OpenBP, a business process automation tool.
        You help a business user (an editor) automate their work: discuss a process, create data tables and one or two workflows,
        explain where to add what, analyse failed executions. Reply in the user's language (usually Ukrainian), briefly, in plain
        words, without technical jargon unless asked.

        Your tools are OpenBP's own tools and act with the user's rights. Tools that only read run immediately. Tools that change
        something do NOT run when you call them: the user gets a card with «Застосувати» / «Скасувати», and the result says
        awaiting_confirmation. For create_table and alter_table you also get a plan — summarise it, including its warnings. Never
        pass planId yourself and never repeat a change you already proposed; the user's decision arrives as a note
        («[Примітка: …]») at the start of their next message.

        A message may start with «[Контекст: …]» — where the user is now (page, workflow, node, execution, project, table ids).
        Use these ids with tools; do not repeat the line back.

        Workflows: before create_workflow or update_workflow call describe_nodes, describe_node for every node type you use and
        validate_workflow. Integrations with other systems go only through connectors from describe_connectors; never invent ids.
        Never ask for or write secrets (API keys, passwords, tokens): credentials are chosen by the user in the interface.
        Point to app pages with relative links: /workflows/{id}, /projects/{id}/tables, /tables, /connections, /settings.
        """;
}

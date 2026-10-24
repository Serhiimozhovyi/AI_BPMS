using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Evaluations;

/// <summary>Вердикт судді.</summary>
/// <param name="Passed">Пройшов.</param>
/// <param name="Reason">Пояснення судді.</param>
public sealed record EvaluationVerdict(bool Passed, string? Reason);

/// <summary>Суддя перевірки <see cref="EvaluationChecks.Llm"/>.</summary>
public interface IEvaluationJudge
{
    /// <summary>Чи відповідає фактичне критеріям і очікуваному.</summary>
    /// <param name="projectId">Проєкт сценарію: credential судді має бути спільним або з цього проєкту.</param>
    /// <param name="config"><c>credentialId</c>, <c>model</c>, <c>criteria</c>.</param>
    /// <param name="expected">Очікуване.</param>
    /// <param name="actual">Фактичне.</param>
    /// <param name="ct">Скасування.</param>
    Task<EvaluationVerdict> JudgeAsync(Guid? projectId, JsonObject config, JsonNode? expected, JsonNode? actual, CancellationToken ct);
}

/// <summary>
/// Модель-суддя через <see cref="LlmChat"/>: credential типу anthropic/openai/openai-compatible, відповідь починається з
/// <c>PASS</c> або <c>FAIL</c>, решта — пояснення.
/// </summary>
/// <param name="contexts">Фабрика контекстів БД.</param>
/// <param name="key">Ключ шифрування credentials.</param>
/// <param name="chat">Розмова з моделлю.</param>
public sealed class LlmEvaluationJudge(IDbContextFactory<OpenBpDbContext> contexts, CredentialEncryptionKey key, LlmChat chat) : IEvaluationJudge
{
    private const string System =
        "You are a strict evaluator of an AI step's output. Compare the ACTUAL output with the EXPECTED output and the CRITERIA. " +
        "Answer with PASS or FAIL on the first line, then one short sentence explaining why, in the language of the criteria.";

    /// <inheritdoc />
    public async Task<EvaluationVerdict> JudgeAsync(Guid? projectId, JsonObject config, JsonNode? expected, JsonNode? actual, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var credentialId = Guid.Parse(config["credentialId"]!.GetValue<string>());
        await using var db = await contexts.CreateDbContextAsync(ct);
        var credential = await db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == credentialId, ct);
        if (credential is null || (credential.ProjectId is { } owner && owner != projectId))
        {
            throw new NotFoundException("credential", credentialId);
        }

        var data = await new CredentialStore(contexts, key).GetDataAsync(credentialId, ct) ?? throw new NotFoundException("credential", credentialId);
        var prompt =
            $"CRITERIA:\n{config["criteria"]?.GetValue<string>() ?? "The actual output must match the expected output in meaning."}\n\n" +
            $"EXPECTED:\n{expected?.ToJsonString(WorkflowJson.Options) ?? "(none)"}\n\n" +
            $"ACTUAL:\n{actual?.ToJsonString(WorkflowJson.Options) ?? "(none)"}";
        LlmReply reply;
        try
        {
            reply = await chat.SendAsync(credential.Type, data, config["model"]!.GetValue<string>(), System, [LlmTurn.User(prompt)], [], ct);
        }
        catch (LlmException ex)
        {
            return new EvaluationVerdict(false, $"Суддя: {ex.Message}");
        }

        var text = reply.Text.Trim();
        var passed = text.StartsWith("PASS", StringComparison.OrdinalIgnoreCase);
        var failed = text.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase);
        var reason = text.Length > 4 && (passed || failed) ? text[4..].Trim(' ', ':', '.', '-', '\n', '\r') : text;
        return new EvaluationVerdict(passed, failed || passed ? (reason.Length == 0 ? null : reason) : $"Суддя відповів не PASS/FAIL: {text}");
    }
}

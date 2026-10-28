using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Application.Assistant;

/// <summary>Модель, доступна для вибору в налаштуваннях помічника.</summary>
/// <param name="Name">Назва, як її приймає провайдер (<c>qwen3:14b</c>).</param>
public sealed record AssistantModel(string Name);

/// <summary>Помилка провайдера моделі (502 <c>ASSISTANT_PROVIDER_ERROR</c>): сервіс моделей недоступний чи відповів помилкою.</summary>
/// <param name="message">Пояснення людською мовою.</param>
public sealed class AssistantProviderException(string message)
    : OpenBpException("ASSISTANT_PROVIDER_ERROR", 502, message);

/// <summary>Список моделей для credential'а помічника (F3-101): для <c>ollama</c> — встановлені моделі з <c>/api/tags</c>.</summary>
public interface IAssistantModelsService
{
    /// <summary>Моделі, які можна обрати для credential'а; для типів без списку — порожньо.</summary>
    /// <param name="credentialId">Credential.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Credential'а немає.</exception>
    /// <exception cref="AssistantProviderException">Ollama недоступна.</exception>
    Task<IReadOnlyList<AssistantModel>> ListAsync(Guid credentialId, CancellationToken ct);
}

/// <summary>Реалізація <see cref="IAssistantModelsService"/>; лише admin (як і налаштування помічника).</summary>
public sealed class AssistantModelsService(
    ICurrentUser currentUser,
    IDbContextFactory<OpenBpDbContext> contexts,
    CredentialEncryptionKey key,
    LlmChat chat) : IAssistantModelsService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AssistantModel>> ListAsync(Guid credentialId, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        string? type;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            type = await db.Credentials.AsNoTracking().Where(c => c.Id == credentialId).Select(c => c.Type).SingleOrDefaultAsync(ct);
        }

        if (type is null)
        {
            throw new NotFoundException("credential", credentialId);
        }

        if (type != "ollama")
        {
            return [];
        }

        var data = await new CredentialStore(contexts, key).GetDataAsync(credentialId, ct) ?? throw new NotFoundException("credential", credentialId);
        try
        {
            return [.. (await chat.ListOllamaModelsAsync(data, ct)).Select(n => new AssistantModel(n))];
        }
        catch (LlmException ex)
        {
            throw new AssistantProviderException(ex.Message);
        }
    }
}

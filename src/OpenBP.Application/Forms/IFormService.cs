using System.Text.Json.Nodes;

namespace OpenBP.Application.Forms;

/// <summary>
/// Публічні форми (F6-03, docs/architecture.md §10.4): тригер <c>core.formTrigger</c> (сторінка <c>/form/{path}</c>) і крок
/// очікування <c>core.wait</c> у режимі <c>form</c> (сторінка <c>/form/resume/{token}</c>). Маршрут
/// <c>/api/v1/forms/{path}</c> анонімний: доступ задає параметр <c>auth</c> вузла (<c>none</c>/<c>login</c>/<c>token</c>),
/// а для очікування секретом є сам токен.
/// </summary>
public interface IFormService
{
    /// <summary>Опис форми для сторінки: заголовок, текст, поля (JSON Schema), підпис кнопки.</summary>
    /// <param name="path">Шлях після <c>/form/</c>: шлях тригера або <c>resume/{token}</c>.</param>
    /// <param name="token">Токен доступу з посилання (<c>auth: token</c>); <c>null</c> — немає.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="Errors.NotFoundException">Форми немає (невідомий шлях, сценарій неактивний, чужий токен).</exception>
    /// <exception cref="FormLoginRequiredException"><c>auth: login</c> без сесії.</exception>
    /// <exception cref="Errors.UnauthenticatedException"><c>auth: token</c> без правильного токена.</exception>
    /// <exception cref="Errors.ConflictException">Форму очікування вже надіслано або виконання її більше не чекає.</exception>
    Task<FormDefinition> GetAsync(string path, string? token, CancellationToken ct);

    /// <summary>
    /// Перевіряє надіслані поля за схемою, зберігає файли (<c>IBinaryStore</c>, сховище <c>postgres</c>) і запускає
    /// виконання (тригер) або будить те, що чекає (крок очікування). Item: <c>{ fields, submittedAt, submittedBy? }</c>,
    /// файли — у <c>binary</c> за назвою поля (у <c>fields</c> — назва файлу).
    /// </summary>
    /// <param name="path">Шлях після <c>/form/</c>.</param>
    /// <param name="submission">Поля, файли і токен доступу.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Виконання і що показати після надсилання.</returns>
    /// <exception cref="FormValidationException">Поля не відповідають схемі, файл не в тому полі або файлів забагато.</exception>
    Task<FormSubmitted> SubmitAsync(string path, FormSubmission submission, CancellationToken ct);
}

/// <summary>Опис форми для сторінки.</summary>
/// <param name="Kind"><c>trigger</c> — форма запускає сценарій; <c>wait</c> — продовжує виконання, що чекає.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Description">Текст над полями.</param>
/// <param name="Schema">Поля (JSON Schema, <c>format: binary</c> — файл).</param>
/// <param name="SubmitLabel">Текст кнопки.</param>
public sealed record FormDefinition(string Kind, string Title, string? Description, JsonObject Schema, string SubmitLabel);

/// <summary>Надіслана форма.</summary>
/// <param name="Fields">Значення полів (без файлів).</param>
/// <param name="Files">Файли за назвою поля.</param>
/// <param name="Token">Токен доступу з посилання (<c>auth: token</c>).</param>
public sealed record FormSubmission(JsonObject Fields, IReadOnlyList<FormUpload> Files, string? Token = null);

/// <summary>Файл з поля форми.</summary>
/// <param name="Field">Назва поля.</param>
/// <param name="FileName">Назва файлу.</param>
/// <param name="ContentType">MIME-тип.</param>
/// <param name="Content">Вміст; читається один раз.</param>
public sealed record FormUpload(string Field, string FileName, string ContentType, Stream Content);

/// <summary>Результат надсилання.</summary>
/// <param name="ExecutionId">Запущене або розбуджене виконання.</param>
/// <param name="Message">Текст для людини після надсилання.</param>
/// <param name="RedirectUrl">Куди перейти після надсилання (лише тригер).</param>
public sealed record FormSubmitted(Guid ExecutionId, string? Message, string? RedirectUrl);

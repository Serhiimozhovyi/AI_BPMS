using OpenBP.Application.Errors;

namespace OpenBP.Application.Forms;

/// <summary>Помилка поля форми.</summary>
/// <param name="Field">Назва поля (властивість схеми); порожня — помилка всієї форми.</param>
/// <param name="Message">Пояснення українською.</param>
public sealed record FormFieldError(string Field, string Message);

/// <summary>400: надіслані дані не відповідають формі. Відповідь містить <c>errors[]</c> за полями.</summary>
public sealed class FormValidationException : OpenBpException
{
    /// <summary>Код помилки.</summary>
    public const string ErrorCode = "FORM_INVALID";

    /// <param name="errors">Помилки за полями.</param>
    public FormValidationException(IReadOnlyList<FormFieldError> errors)
        : base(ErrorCode, 400, errors is { Count: 1 } ? errors[0].Message : $"Перевірте поля форми: {errors?.Count ?? 0} помилок.")
    {
        Errors = errors ?? [];
    }

    /// <summary>Помилки; у REST — поле <c>errors</c> problem-details.</summary>
    public IReadOnlyList<FormFieldError> Errors { get; }
}

/// <summary>401: форма лише для користувачів OpenBP (<c>auth: login</c>), а сесії немає. Сторінка веде на <see cref="LoginUrl"/>.</summary>
public sealed class FormLoginRequiredException : OpenBpException
{
    /// <summary>Код помилки.</summary>
    public const string ErrorCode = "FORM_LOGIN_REQUIRED";

    /// <param name="loginUrl">Сторінка входу з поверненням на форму.</param>
    public FormLoginRequiredException(string loginUrl)
        : base(ErrorCode, 401, "Щоб надіслати цю форму, увійдіть до OpenBP.")
    {
        LoginUrl = loginUrl;
    }

    /// <summary>Сторінка входу з <c>returnUrl</c> на форму.</summary>
    public string LoginUrl { get; }
}

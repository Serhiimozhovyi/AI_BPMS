using OpenBP.Application.Errors;

namespace OpenBP.Application.Tasks;

/// <summary>403: задача іншого виконавця.</summary>
public sealed class TaskForbiddenException : OpenBpException
{
    /// <summary>Код помилки.</summary>
    public const string ErrorCode = "TASK_FORBIDDEN";

    /// <summary>Створює помилку.</summary>
    public TaskForbiddenException()
        : base(ErrorCode, 403, "Це задача іншого виконавця: прийняти рішення може лише він або адміністратор.")
    {
    }
}

/// <summary>400: такого рішення в задачі немає.</summary>
public sealed class TaskDecisionInvalidException : OpenBpException
{
    /// <summary>Код помилки.</summary>
    public const string ErrorCode = "TASK_DECISION_INVALID";

    /// <param name="decision">Надіслане рішення.</param>
    /// <param name="keys">Допустимі ключі.</param>
    public TaskDecisionInvalidException(string? decision, IEnumerable<string> keys)
        : base(ErrorCode, 400, $"Рішення «{decision}» немає в задачі. Оберіть одне з: {string.Join(", ", keys)}.")
    {
    }
}

using System.Globalization;

namespace OpenBP.Application.Forms;

/// <summary>
/// Серверні ліміти надсилання форм (F6-03): розмір тіла запиту (поля й файли разом) і кількість файлів.
/// Змінні середовища Api — <see cref="MaxBodyMbKey"/>, <see cref="MaxFilesKey"/> (docs/admin/env.md).
/// </summary>
/// <param name="MaxBodyBytes">Найбільший розмір тіла запиту, байт.</param>
/// <param name="MaxFiles">Найбільша кількість файлів в одному надсиланні.</param>
public sealed record FormLimits(long MaxBodyBytes, int MaxFiles)
{
    /// <summary>Ключ конфігурації: найбільший розмір тіла, МБ (типово 10).</summary>
    public const string MaxBodyMbKey = "OPENBP_FORM_MAX_BODY_MB";

    /// <summary>Ключ конфігурації: найбільша кількість файлів (типово 10).</summary>
    public const string MaxFilesKey = "OPENBP_FORM_MAX_FILES";

    /// <summary>Типові ліміти: 10 МБ і 10 файлів.</summary>
    public static FormLimits Default { get; } = new(10L * 1024 * 1024, 10);

    /// <summary>Ліміти з конфігурації; порожні значення — типові.</summary>
    /// <param name="maxBodyMb">Значення <see cref="MaxBodyMbKey"/>.</param>
    /// <param name="maxFiles">Значення <see cref="MaxFilesKey"/>.</param>
    /// <exception cref="ArgumentException">Значення не є додатним цілим (МБ — до 1024, файлів — до 100).</exception>
    public static FormLimits Parse(string? maxBodyMb, string? maxFiles)
    {
        var mb = Positive(maxBodyMb, MaxBodyMbKey, 1024) ?? 10;
        var files = Positive(maxFiles, MaxFilesKey, 100) ?? Default.MaxFiles;
        return new FormLimits(mb * 1024L * 1024, files);
    }

    private static int? Positive(string? value, string key, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 && n <= max
            ? n
            : throw new ArgumentException($"{key} має бути цілим числом від 1 до {max}, а задано «{value}».");
    }
}

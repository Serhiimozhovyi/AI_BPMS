using OpenBP.Shared.Workflow;

namespace OpenBP.Engine;

/// <summary>
/// Файлове сховище бінарних даних (docs/architecture.md §6): байти живуть у локальній папці або S3-сумісному
/// сховищі, в items лише <see cref="BinaryRef"/>. Вузли отримують його через <c>INodeContext.Binary</c>.
/// Реалізації: локальна/S3 у хостах і <c>InMemoryBinaryStore</c> у <c>OpenBP.Engine.Testing</c>.
/// </summary>
public interface IBinaryStore
{
    /// <summary>
    /// Зберігає вміст потоку і повертає посилання з заповненими <c>id</c>, <c>mimeType</c>, <c>fileSize</c>,
    /// <c>sha256</c> та, якщо відомі, <c>fileName</c>/<c>fileExtension</c>.
    /// </summary>
    /// <param name="content">Вміст файлу; читається до кінця, не закривається.</param>
    /// <param name="mimeType">MIME-тип, напр. <c>application/pdf</c>.</param>
    /// <param name="fileName">Оригінальна назва файлу, якщо є.</param>
    /// <param name="ct">Скасування.</param>
    Task<BinaryRef> PutAsync(Stream content, string mimeType, string? fileName, CancellationToken ct);

    /// <summary>Відкриває вміст файлу на читання; потік закриває той, хто його відкрив.</summary>
    /// <param name="reference">Посилання з item'а.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BinaryNotFoundException">Файлу з таким <see cref="BinaryRef.Id"/> немає.</exception>
    Task<Stream> OpenReadAsync(BinaryRef reference, CancellationToken ct);
}

using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Nodes;

/// <summary>
/// Усе, що вузол отримує від движка під час одного запуску (docs/connectors.md §1).
/// Реалізацію для бою будує движок (F1-04); для тестів вузлів — <c>FakeNodeContext</c> у <c>OpenBP.Engine.Testing</c>.
/// </summary>
public interface INodeContext
{
    /// <summary>Визначення вузла зі сценарію: id, назва, параметри до обчислення, retry/timeout.</summary>
    NodeDefinition Node { get; }

    /// <summary>Ідентифікатор поточного виконання.</summary>
    Guid ExecutionId { get; }

    /// <summary>Items із вхідного порту в порядку <see cref="NodeDescriptor.Inputs"/>; для тригерів — порожній список.</summary>
    /// <param name="input">Індекс входу (від 0).</param>
    IReadOnlyList<Item> Items(int input = 0);

    /// <summary>
    /// Параметри вузла з обчисленими <c>{{ }}</c> для item'а з індексом <paramref name="itemIndex"/>
    /// на основному вході, приведені до <typeparamref name="T"/>. Для вузлів без входу (тригери) —
    /// індекс 0 з порожнім <c>$json</c>.
    /// </summary>
    /// <typeparam name="T">POCO, що відповідає JSON Schema параметрів вузла.</typeparam>
    /// <param name="itemIndex">Індекс item'а на основному вході.</param>
    T Params<T>(int itemIndex);

    /// <summary>
    /// Розшифровані дані credential'а за <b>назвою</b> з <see cref="NodeDescriptor.Credentials"/>:
    /// id береться з параметра вузла, вказаного у <see cref="CredentialRequirement.Parameter"/>.
    /// Доступно тільки у воркері; у CLI/тестах — з фейкового провайдера.
    /// </summary>
    /// <param name="name">Назва вимоги credential'а з Descriptor.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="CredentialNotFoundException">Credential не існує або параметр з id порожній.</exception>
    Task<JsonObject> GetCredentialAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Виходи вже виконаних вузлів за <b>назвою</b> вузла: items основного виходу (порт 0) — те саме, що
    /// <c>$node["Назва"]</c> у виразах. Лише для читання: вузол не змінює ці items.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<Item>> NodeOutputs { get; }

    /// <summary>Часовий пояс сценарію (IANA, <c>settings.timezone</c>), напр. <c>Europe/Kyiv</c>.</summary>
    string Timezone { get; }

    /// <summary>Змінні інстансу (<c>$vars</c>, F6-09): ключ → значення; не секрети.</summary>
    IReadOnlyDictionary<string, string> Vars => new Dictionary<string, string>();

    /// <summary>Журнал виконання; <c>nodeId</c> підставляє вузол сам (<see cref="Node"/>).</summary>
    IExecutionLogger Logger { get; }

    /// <summary>Файлове сховище для бінарних даних item'ів.</summary>
    IBinaryStore Binary { get; }

    /// <summary>
    /// Стан вузла між виконаннями (таблиця <c>node_state</c>) — для polling-тригерів (<c>mcp.poll</c>: <c>seenKeys</c>).
    /// Движок зберігає значення після успішного запуску вузла.
    /// </summary>
    JsonObject NodeState { get; set; }

    /// <summary>
    /// Додає підкрок до журналу цього запуску вузла (F6-12: запит до моделі, виклик інструмента агентом). Движок зберігає
    /// підкроки останньої спроби разом із кроком — і при успіху, і при помилці (часткове трасування). Без секретів.
    /// </summary>
    /// <param name="step">Підкрок.</param>
    void RecordSubStep(Execution.SubStep step)
    {
    }

    /// <summary>
    /// Пробудження після <see cref="Suspend"/> (F5-02): стан, який вузол передав, і запланований час; <c>null</c> —
    /// звичайний запуск.
    /// </summary>
    NodeResumption? Resumption => null;

    /// <summary>
    /// Призупиняє виконання до <paramref name="resumeAt"/> (довге очікування, F5-02): вузол повертає результат цього
    /// виклику, движок пише крок <c>waiting</c>, не планує наступників і звільняє воркер. На пробудженні вузол
    /// запускається знову з тими самими items і <see cref="Resumption"/>. Секретів у <paramref name="state"/> не класти.
    /// </summary>
    /// <param name="resumeAt">Коли розбудити (UTC).</param>
    /// <param name="state">Невеликий стан вузла до пробудження.</param>
    /// <returns>Значення, яке вузол повертає з <see cref="INode.ExecuteAsync"/>.</returns>
    /// <exception cref="NotSupportedException">Контекст не підтримує призупинення.</exception>
    Item[][] Suspend(DateTimeOffset resumeAt, JsonObject? state = null) =>
        throw new NotSupportedException("Цей контекст не підтримує довге очікування.");
}

/// <summary>Пробудження вузла після призупинення (F5-02).</summary>
/// <param name="ResumeAt">Запланований час пробудження.</param>
/// <param name="State">Стан, переданий у <see cref="INodeContext.Suspend"/> (порожній, якщо не передано).</param>
public sealed record NodeResumption(DateTimeOffset ResumeAt, JsonObject State);

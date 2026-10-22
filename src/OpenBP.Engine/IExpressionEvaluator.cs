using System.Text.Json.Nodes;
using OpenBP.Engine.Expressions;

namespace OpenBP.Engine;

/// <summary>
/// Обчислювач виразів <c>{{ &lt;js&gt; }}</c> у рядкових параметрах (docs/architecture.md §2, §5).
/// Реалізації: <c>JintExpressionEvaluator</c> з лімітами (F1-06) і <c>FakeExpressionEvaluator</c>
/// у <c>OpenBP.Engine.Testing</c>. Движок не знає про Jint — лише про цей інтерфейс.
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>
    /// Обчислює рядок параметра для одного item'а:
    /// <list type="bullet">
    /// <item>рядок без <c>{{ }}</c> повертається як є (рядкове значення);</item>
    /// <item>рядок, що цілком є одним виразом (<c>"{{ $json.lines }}"</c>), повертає значення виразу
    /// зі збереженням типу — об'єкт, масив, число, <c>null</c>;</item>
    /// <item>рядок із вкрапленнями (<c>"Замовлення №{{ $json.number }}"</c>) повертає рядок із підставленими значеннями.</item>
    /// </list>
    /// Приведення результату до типу зі схеми параметрів робить той, хто будує параметри вузла (<c>INodeContext.Params</c>).
    /// </summary>
    /// <param name="expression">Рядок параметра, можливо з <c>{{ }}</c>.</param>
    /// <param name="scope">Змінні, доступні виразу.</param>
    /// <exception cref="ExpressionException">Синтаксична помилка, помилка виконання або перевищення лімітів.</exception>
    JsonNode? Evaluate(string expression, ExpressionScope scope);
}

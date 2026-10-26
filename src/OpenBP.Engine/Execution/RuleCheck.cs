using System.Text.Json.Nodes;

namespace OpenBP.Engine.Execution;

/// <summary>Відхилення правила переходу (ADR-004 §3): дозвіл пройти перехід, попри невиконане правило.</summary>
/// <param name="StageId">Етап, з якого перехід.</param>
/// <param name="RuleId">Правило етапу.</param>
public sealed record RuleOverride(string StageId, string RuleId);

/// <summary>
/// Результат перевірки одного правила переходу на ребрі з етапу (ADR-004 §3) — з нього хост веде порушення
/// (<c>violations</c>): невиконане й не відхилене правило відкриває порушення, виконане — закриває відкрите.
/// </summary>
/// <param name="StageId">Етап, з якого перехід.</param>
/// <param name="RuleId">Правило (<c>transitions[].rules[].id</c>).</param>
/// <param name="Blocking"><c>mode: block</c> — невиконане правило зупиняє перехід; інакше <c>warn</c>.</param>
/// <param name="Passed">Умова істинна для кожного item на ребрі.</param>
/// <param name="Overridden">Правило не виконане, але для нього є відхилення (<see cref="RuleOverride"/>).</param>
/// <param name="Message">Повідомлення правила для людини.</param>
/// <param name="NodeId">Вузол поза етапом, куди веде перехід.</param>
/// <param name="Context">Items на ребрі (<c>json</c>), ще не очищені від секретів — це робить хост.</param>
/// <param name="At">Коли перевірено.</param>
public sealed record RuleCheck(
    string StageId,
    string RuleId,
    bool Blocking,
    bool Passed,
    bool Overridden,
    string? Message,
    string NodeId,
    JsonArray Context,
    DateTimeOffset At);

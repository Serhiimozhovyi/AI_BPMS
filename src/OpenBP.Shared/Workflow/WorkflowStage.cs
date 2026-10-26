using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBP.Shared.Workflow;

/// <summary>
/// Етап бізнес-процесу (ADR-004, docs/workflow-format.md §«Етапи»): іменована група вузлів того самого графа з виконавцем,
/// строком і правилами переходу. Необов'язковий шар над графом: одиниця виконання лишається вузол, стан етапу рушій
/// обчислює з виконання (F7-02), правила перевіряє на ребрах, що виходять з етапу (F7-03).
/// </summary>
public sealed class WorkflowStage
{
    /// <summary>Унікальний у межах сценарію; на нього посилаються <c>transitions[].to</c>.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Id вузлів етапу; кожен вузол належить щонайбільше одному етапу, вузли без етапу дозволені.</summary>
    public List<string> Nodes { get; set; } = [];

    /// <summary>Хто виконує етап; <c>null</c> — не задано.</summary>
    public StageOwner? Owner { get; set; }

    /// <summary>Строк від входу в етап; <c>null</c> — без строку.</summary>
    public StageDue? Due { get; set; }

    /// <summary>Правила переходів з етапу; <c>null</c> або порожній — переходи без правил.</summary>
    public List<StageTransition>? Transitions { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Виконавець етапу: доріжка бізнес-вигляду, роль або вираз (email чи id користувача).</summary>
public sealed class StageOwner
{
    /// <summary>Id доріжки з <c>business.lanes</c>.</summary>
    public string? Lane { get; set; }

    public string? Role { get; set; }

    /// <summary>Вираз <c>{{ }}</c>, що дає виконавця за даними виконання.</summary>
    public string? Expression { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Строк етапу: <c>amount</c>/<c>unit</c> від входу в етап або вираз дати <c>until</c>.</summary>
public sealed class StageDue
{
    public int? Amount { get; set; }

    /// <summary>Одна з <see cref="StageDueUnits"/>.</summary>
    public string? Unit { get; set; }

    /// <summary>Вираз, що дає дату-час завершення.</summary>
    public string? Until { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Одиниці строку етапу.</summary>
public static class StageDueUnits
{
    public const string Minutes = "minutes";

    public const string Hours = "hours";

    public const string Days = "days";

    /// <summary>Робочі дні: пн–пт за <c>settings.timezone</c>, без календаря свят.</summary>
    public const string BusinessDays = "businessDays";

    public static IReadOnlyList<string> All { get; } = [Minutes, Hours, Days, BusinessDays];
}

/// <summary>Правила на переходах з етапу в інший етап (<c>to</c>) або на всіх виходах (без <c>to</c>).</summary>
public sealed class StageTransition
{
    /// <summary>Id етапу-призначення; <c>null</c> — правила діють на всі ребра, що виходять з етапу.</summary>
    public string? To { get; set; }

    public List<StageRule> Rules { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Правило переходу: умова, яка має бути істинною, інакше — блокування або попередження з порушенням.</summary>
public sealed class StageRule
{
    /// <summary>Унікальний у межах етапу; на нього посилаються порушення і відхилення.</summary>
    public string Id { get; set; } = "";

    /// <summary>Вираз над items переходу; <c>true</c> — правило виконане.</summary>
    public string Condition { get; set; } = "";

    /// <summary><see cref="StageRuleModes.Block"/> або <see cref="StageRuleModes.Warn"/>; <c>null</c> — <c>warn</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>Чи правило блокує перехід (<see cref="StageRuleModes.Block"/>); без <see cref="Mode"/> — попередження.</summary>
    [JsonIgnore]
    public bool IsBlocking => Mode == StageRuleModes.Block;

    /// <summary>Повідомлення для людини, коли правило не виконане.</summary>
    public string? Message { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Режими правила переходу.</summary>
public static class StageRuleModes
{
    /// <summary>Перехід зупиняється у <c>waiting</c> до виправлення або відхилення з причиною.</summary>
    public const string Block = "block";

    /// <summary>Перехід проходить, порушення фіксується.</summary>
    public const string Warn = "warn";
}

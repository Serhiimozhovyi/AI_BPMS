using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBP.Shared.Workflow;

/// <summary>
/// Бізнес-розмітка сценарію для бізнес-вигляду (docs/business-view.md §«Бізнес-атрибути у форматі»):
/// доріжки учасників і кроки людей у зовнішніх системах. Усе необов'язкове; рушій ці поля ігнорує.
/// </summary>
public sealed class WorkflowBusiness
{
    /// <summary>Доріжки (учасники) в порядку показу; <c>null</c> — доріжки за правилами за замовчуванням.</summary>
    public List<BusinessLane>? Lanes { get; set; }

    /// <summary>Кроки людей у зовнішніх системах, яких немає в графі; кожен має хоча б один зв'язок із вузлом.</summary>
    public List<BusinessStep>? Steps { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Доріжка бізнес-вигляду: хто виконує кроки («Кіт 2», «OpenBP», «Сповіщення»).</summary>
public sealed class BusinessLane
{
    /// <summary>Унікальний у межах сценарію; на нього посилаються <c>node.business.lane</c> і <c>steps[].lane</c>.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Підпис під назвою («люди у веб-формах»).</summary>
    public string? Description { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Зовнішній крок: дія людини в іншій системі, пов'язана з вузлами сценарію.</summary>
public sealed class BusinessStep
{
    /// <summary>Унікальний серед кроків сценарію.</summary>
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Subtitle { get; set; }

    /// <summary>Id доріжки з <see cref="WorkflowBusiness.Lanes"/>.</summary>
    public string Lane { get; set; } = "";

    /// <summary>Зв'язки з вузлами графа; без них крок на схемі нічого не означає, тому потрібен хоча б один.</summary>
    public List<BusinessLink> Links { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Зв'язок зовнішнього кроку з вузлом: стрілка на схемі.</summary>
public sealed class BusinessLink
{
    /// <summary>Id вузла сценарію.</summary>
    public string Node { get; set; } = "";

    /// <summary><see cref="BusinessLinkDirections.In"/> — вузол читає результат кроку; <see cref="BusinessLinkDirections.Out"/> — вузол створює крок.</summary>
    public string Direction { get; set; } = BusinessLinkDirections.In;

    /// <summary>Підпис стрілки («читає статус»).</summary>
    public string? Label { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Напрями зв'язку <see cref="BusinessLink"/>.</summary>
public static class BusinessLinkDirections
{
    /// <summary>Крок → вузол: вузол читає те, що зробила людина (пунктирна стрілка).</summary>
    public const string In = "in";

    /// <summary>Вузол → крок: вузол ставить людині задачу (суцільна стрілка).</summary>
    public const string Out = "out";
}

/// <summary>Бізнес-атрибути вузла: як він показується в бізнес-вигляді.</summary>
public sealed class NodeBusiness
{
    /// <summary>Назва кроку людською мовою; без неї — <c>name</c> вузла.</summary>
    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    /// <summary>Id доріжки з <c>business.lanes</c>; без нього — доріжка за типом вузла.</summary>
    public string? Lane { get; set; }

    /// <summary><c>true</c> — не показувати вузол; <c>false</c> — показувати технічний вузол, який інакше згорнувся б.</summary>
    public bool? Hidden { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Бізнес-атрибути ребра.</summary>
public sealed class ConnectionBusiness
{
    /// <summary>Підпис стрілки («2 дні без рішення»); без нього — назва виходу.</summary>
    public string? Label { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

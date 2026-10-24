using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Application.Auth;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>Шаблон для «Почати з прикладу» (F3-59).</summary>
/// <param name="Id">Ім'я файлу в <c>examples/</c> без <c>.json</c>.</param>
/// <param name="Name">Назва сценарію-прикладу.</param>
/// <param name="Description">Що робить приклад.</param>
/// <param name="Definition">Визначення без <c>pinData</c>, з порожніми <c>serverId</c>/<c>credentialId</c> і без сценарію помилок.</param>
/// <param name="Connectors">Залежності (F6-16): id записів каталогу конекторів, без яких шаблон не запрацює.</param>
public sealed record WorkflowTemplate(string Id, string Name, string Description, WorkflowDefinition Definition, IReadOnlyList<string> Connectors);

/// <summary>Шаблони сценаріїв з прикладів репозиторію. Читає будь-який автентифікований користувач.</summary>
public interface IWorkflowTemplateService
{
    /// <summary>Шаблони в порядку каталогу.</summary>
    /// <param name="ct">Скасування.</param>
    Task<IReadOnlyList<WorkflowTemplate>> ListAsync(CancellationToken ct);
}

/// <summary>
/// Сирі шаблони: каталог <c>examples/templates.json</c> (<c>{ templates: [{ file, description, connectors? }] }</c>) і файли, на які він
/// посилається. Хост завантажує їх зі своїх вбудованих ресурсів (<see cref="FromResources"/>).
/// </summary>
public sealed class WorkflowTemplateCatalog
{
    /// <summary>Ім'я каталогу серед ресурсів.</summary>
    public const string CatalogFile = "templates.json";

    private readonly IReadOnlyList<WorkflowTemplate> templates;

    /// <summary>Розбирає каталог; <paramref name="readFile"/> повертає вміст файлу прикладу або <c>null</c>.</summary>
    /// <param name="catalogJson">Вміст <c>templates.json</c>.</param>
    /// <param name="readFile">Файл прикладу за іменем.</param>
    /// <exception cref="InvalidOperationException">Каталог посилається на відсутній або невалідний файл.</exception>
    public WorkflowTemplateCatalog(string catalogJson, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(readFile);
        var entries = JsonNode.Parse(catalogJson)?["templates"] as JsonArray ?? [];
        var list = new List<WorkflowTemplate>();
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var file = entry["file"]?.GetValue<string>() ?? throw new InvalidOperationException("Шаблон без поля file.");
            var json = readFile(file) ?? throw new InvalidOperationException($"Немає файлу шаблону «{file}».");
            var definition = JsonSerializer.Deserialize<WorkflowDefinition>(json, WorkflowJson.Options)
                ?? throw new InvalidOperationException($"Файл шаблону «{file}» порожній.");
            list.Add(new WorkflowTemplate(
                Path.GetFileNameWithoutExtension(file),
                definition.Name,
                entry["description"]?.GetValue<string>() ?? "",
                Sanitize(definition),
                [.. (entry["connectors"] as JsonArray ?? []).Select(c => c?.GetValue<string>()).OfType<string>()]));
        }

        templates = list;
    }

    /// <summary>Шаблони в порядку каталогу.</summary>
    public IReadOnlyList<WorkflowTemplate> Templates => templates;

    /// <summary>Каталог із вбудованих ресурсів збірки з логічними іменами <c>{prefix}{file}</c>.</summary>
    /// <param name="assembly">Збірка з ресурсами.</param>
    /// <param name="prefix">Префікс логічних імен, наприклад <c>examples/</c>.</param>
    public static WorkflowTemplateCatalog FromResources(Assembly assembly, string prefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? Read(string file)
        {
            using var stream = assembly.GetManifestResourceStream(prefix + file);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        return new WorkflowTemplateCatalog(Read(CatalogFile) ?? """{ "templates": [] }""", Read);
    }

    /// <summary>
    /// Прибирає з прикладу те, що належить середовищу, де його писали: <c>pinData</c>, посилання на конектори,
    /// credentials і сценарій помилок. Порожній <c>serverId</c> — помилка валідації на вузлі, яку автор бачить одразу.
    /// </summary>
    private static WorkflowDefinition Sanitize(WorkflowDefinition definition)
    {
        definition.PinData = [];
        definition.Settings.ErrorWorkflowId = null;
        foreach (var node in definition.Nodes)
        {
            Clear(node.Parameters);
        }

        return definition;
    }

    private static void Clear(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (name is "serverId" or "credentialId" && obj[name] is JsonValue)
                    {
                        obj[name] = "";
                    }
                    else
                    {
                        Clear(obj[name]);
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Clear(child);
                }

                break;
        }
    }
}

/// <summary>Реалізація <see cref="IWorkflowTemplateService"/> поверх <see cref="WorkflowTemplateCatalog"/>.</summary>
public sealed class WorkflowTemplateService(WorkflowTemplateCatalog catalog, ICurrentUser currentUser) : IWorkflowTemplateService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<WorkflowTemplate>> ListAsync(CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        return Task.FromResult(catalog.Templates);
    }
}

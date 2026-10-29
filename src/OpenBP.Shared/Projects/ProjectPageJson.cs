using System.Reflection;

namespace OpenBP.Shared.Projects;

/// <summary>
/// JSON Schema сторінки застосунку проєкту (<c>Schemas/project-page.schema.json</c>, docs/projects.md §3): контракт для
/// конструктора, MCP і шаблонів. Перевіряє сервер (<c>OpenBP.Application</c>); схема — опис і підказки редакторам.
/// </summary>
public static class ProjectPageJson
{
    private const string SchemaResource = "OpenBP.Shared.Schemas.project-page.schema.json";

    private static readonly Lazy<string> SchemaText = new(LoadSchema);

    /// <summary>Текст JSON Schema сторінки.</summary>
    public static string Schema => SchemaText.Value;

    private static string LoadSchema()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SchemaResource)
            ?? throw new InvalidOperationException($"Ресурс {SchemaResource} не знайдено.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

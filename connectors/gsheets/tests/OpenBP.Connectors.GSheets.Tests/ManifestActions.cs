using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace OpenBP.Connectors.GSheets.Tests;

/// <summary>
/// Контракт <c>actions</c> маніфесту (F3-92, docs/connectors.md §5.1): імена дій у <c>connector.json</c> збігаються з tools, які
/// сервер віддає в <c>tools/list</c>, і кожна дія має назву. Новий tool без запису в маніфесті валить тест.
/// <c>OPENBP_ACTIONS_DUMP=шлях</c> замість перевірки пише JSON поточних tools (для оновлення маніфесту).
/// </summary>
internal static class ManifestActions
{
    /// <summary>Перевіряє <c>actions</c> маніфесту <c>connector.json</c> конектора <paramref name="connectorId"/> (<c>null</c> — не звіряти id).</summary>
    public static void Verify(string? connectorId, IEnumerable<McpClientTool> tools)
    {
        var listed = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        if (Environment.GetEnvironmentVariable("OPENBP_ACTIONS_DUMP") is { Length: > 0 } dump)
        {
            var array = new JsonArray(listed.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["title"] = string.IsNullOrWhiteSpace(t.ProtocolTool.Title) ? t.Name : t.ProtocolTool.Title,
                ["description"] = t.Description,
            }).ToArray());
            File.WriteAllText(dump, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return;
        }

        var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath()), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsObject();
        if (connectorId is not null)
        {
            Assert.Equal(connectorId, manifest["id"]!.GetValue<string>());
        }

        var actions = Assert.IsType<JsonArray>(manifest["actions"]).Select(a => a!.AsObject()).ToList();
        Assert.Equal(listed.Select(t => t.Name), actions.Select(a => a["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.All(actions, a => Assert.False(string.IsNullOrWhiteSpace(a["title"]?.GetValue<string>()), a["name"]!.GetValue<string>()));
    }

    private static string ManifestPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "connector.json");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("connector.json не знайдено вище каталогу тестів.");
    }
}

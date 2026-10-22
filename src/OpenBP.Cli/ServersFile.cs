using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBP.Mcp.Client;

namespace OpenBP.Cli;

/// <summary>
/// Файл MCP-серверів для <c>--servers</c>:
/// <code>
/// { "servers": [
///   { "id": "srv_1c", "name": "1С", "transport": "stdio", "command": "dotnet", "args": ["…"], "env": { }, "cwd": "…" },
///   { "id": "srv_tg", "name": "Telegram", "transport": "http", "url": "http://localhost:5100/mcp" } ] }
/// </code>
/// Відносний <c>cwd</c> рахується від теки файлу; без <c>cwd</c> stdio-сервер стартує в теці файлу.
/// Секретів у файлі не тримайте: CLI призначений для моків і dev-серверів.
/// </summary>
internal static class ServersFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Читає і перевіряє файл серверів.</summary>
    /// <param name="path">Шлях до файлу.</param>
    /// <exception cref="CliException">Файл не читається або має помилки.</exception>
    public static IReadOnlyList<McpServerConfig> Load(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(File.ReadAllText(full), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CliException($"Не вдалося прочитати файл серверів {path}: {ex.Message}");
        }

        var result = new List<McpServerConfig>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document?.Servers ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                throw new CliException($"{path}: у сервера немає id.");
            }

            if (!ids.Add(entry.Id))
            {
                throw new CliException($"{path}: сервер «{entry.Id}» оголошено двічі.");
            }

            result.Add(entry.Transport switch
            {
                McpTransportKind.Stdio when !string.IsNullOrWhiteSpace(entry.Command) => new McpServerConfig
                {
                    Id = entry.Id,
                    Name = entry.Name ?? entry.Id,
                    Transport = McpTransportKind.Stdio,
                    Command = entry.Command,
                    Arguments = entry.Args ?? [],
                    Environment = entry.Env,
                    WorkingDirectory = entry.Cwd is null ? directory : Path.GetFullPath(entry.Cwd, directory),
                },
                McpTransportKind.Http when Uri.TryCreate(entry.Url, UriKind.Absolute, out var url) => new McpServerConfig
                {
                    Id = entry.Id,
                    Name = entry.Name ?? entry.Id,
                    Transport = McpTransportKind.Http,
                    Url = url,
                },
                McpTransportKind.Stdio => throw new CliException($"{path}: stdio-сервер «{entry.Id}» без command."),
                _ => throw new CliException($"{path}: http-сервер «{entry.Id}» без коректного url."),
            });
        }

        return result;
    }

    private sealed record Document(IReadOnlyList<Entry>? Servers);

    private sealed record Entry(
        string Id,
        string? Name,
        McpTransportKind Transport,
        string? Command,
        IReadOnlyList<string>? Args,
        IReadOnlyDictionary<string, string?>? Env,
        string? Cwd,
        string? Url);
}

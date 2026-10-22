using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Testing;

namespace OpenBP.Cli;

/// <summary>
/// Файл credentials для <c>--credentials</c>: <c>{ "&lt;credentialId&gt;": { … } }</c>, напр.
/// <c>{ "cred_ollama": { "baseUrl": "http://localhost:11434/v1" } }</c>. Дані віддаються вузлам як є, без шифрування:
/// CLI призначений для dev-запусків — не кладіть у файл і в репо бойові ключі.
/// </summary>
internal static class CredentialsFile
{
    /// <summary>Читає файл у <see cref="FakeCredentialProvider"/>.</summary>
    /// <param name="path">Шлях до файлу.</param>
    /// <exception cref="CliException">Файл не читається або не є об'єктом об'єктів.</exception>
    public static FakeCredentialProvider Load(string path)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new CliException($"{path}: очікується JSON-об'єкт {{ \"<credentialId>\": {{ … }} }}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CliException($"Не вдалося прочитати файл credentials {path}: {ex.Message}");
        }

        var provider = new FakeCredentialProvider();
        foreach (var (id, data) in root)
        {
            provider.Add(id, data as JsonObject ?? throw new CliException($"{path}: credential «{id}» має бути JSON-об'єктом."));
        }

        return provider;
    }
}

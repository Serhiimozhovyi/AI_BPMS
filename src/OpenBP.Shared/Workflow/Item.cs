using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenBP.Shared.Workflow;

/// <summary>Одиниця даних між вузлами (docs/architecture.md §2).</summary>
public sealed class Item
{
    public JsonObject Json { get; set; } = new();

    /// <summary>Бінарні вкладення: ключ → посилання на файл у сховищі.</summary>
    public Dictionary<string, BinaryRef>? Binary { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Посилання на бінарні дані у файловому сховищі. Самі байти в item не потрапляють —
/// лише метадані (docs/workflow-format.md §«BinaryRef»).
/// </summary>
public sealed class BinaryRef
{
    /// <summary>Ключ файлу у сховищі.</summary>
    public string Id { get; set; } = "";

    public string MimeType { get; set; } = "application/octet-stream";

    public string? FileName { get; set; }

    /// <summary>Розширення без крапки, напр. <c>pdf</c>.</summary>
    public string? FileExtension { get; set; }

    /// <summary>Розмір у байтах.</summary>
    public long? FileSize { get; set; }

    /// <summary>Провайдер сховища: <c>local</c> або <c>s3</c>.</summary>
    public string? Storage { get; set; }

    /// <summary>Каталог / префікс у сховищі.</summary>
    public string? Directory { get; set; }

    /// <summary>SHA-256 вмісту, hex у нижньому регістрі.</summary>
    public string? Sha256 { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

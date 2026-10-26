using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBP.Connectors.Http.Tools;

/// <summary>Запис стрічки (<c>rss_read</c>): поля — як у вузла n8n «RSS Read».</summary>
/// <param name="Title">Заголовок.</param>
/// <param name="Link">Посилання.</param>
/// <param name="IsoDate">Дата публікації, ISO 8601 UTC.</param>
/// <param name="PubDate">Дата публікації як у стрічці.</param>
/// <param name="ContentSnippet">Короткий опис без розмітки.</param>
/// <param name="Content">Повний вміст (HTML) або опис.</param>
/// <param name="Creator">Автор.</param>
/// <param name="Categories">Категорії.</param>
/// <param name="Guid">Ідентифікатор запису.</param>
public sealed record RssItemResult(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("link")] string? Link,
    [property: JsonPropertyName("isoDate")] string? IsoDate,
    [property: JsonPropertyName("pubDate")] string? PubDate,
    [property: JsonPropertyName("contentSnippet")] string? ContentSnippet,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("creator")] string? Creator,
    [property: JsonPropertyName("categories")] IReadOnlyList<string> Categories,
    [property: JsonPropertyName("guid")] string? Guid);

/// <summary>Результат <c>rss_read</c>.</summary>
/// <param name="FeedTitle">Назва стрічки.</param>
/// <param name="FeedLink">Сайт стрічки.</param>
/// <param name="Items">Записи в порядку стрічки.</param>
public sealed record RssResult(
    [property: JsonPropertyName("feedTitle")] string? FeedTitle,
    [property: JsonPropertyName("feedLink")] string? FeedLink,
    [property: JsonPropertyName("items")] IReadOnlyList<RssItemResult> Items);

/// <summary>Результат <c>http_request</c>.</summary>
/// <param name="Status">HTTP-статус.</param>
/// <param name="Ok">Статус 2xx.</param>
/// <param name="Reason">Пояснення статусу.</param>
/// <param name="Url">Адреса після редіректів, без параметрів запиту.</param>
/// <param name="Headers">Заголовки відповіді (імена в нижньому регістрі).</param>
/// <param name="ContentType">Тип вмісту без параметрів.</param>
/// <param name="Json">Розібране тіло, якщо воно JSON.</param>
/// <param name="Text">Тіло як текст, якщо воно текстове, але не JSON.</param>
/// <param name="Base64">Тіло в base64, якщо воно двійкове.</param>
public sealed record HttpResult(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string> Headers,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("json")] JsonElement? Json = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("base64")] string? Base64 = null);

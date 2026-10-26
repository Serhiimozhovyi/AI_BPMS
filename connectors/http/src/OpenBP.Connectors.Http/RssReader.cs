using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace OpenBP.Connectors.Http;

/// <summary>Запис стрічки: поля, звичні за вузлом n8n «RSS Read».</summary>
/// <param name="Title">Заголовок.</param>
/// <param name="Link">Посилання.</param>
/// <param name="IsoDate">Дата публікації в ISO 8601 (UTC), якщо вдалося розібрати.</param>
/// <param name="PubDate">Дата публікації як у стрічці.</param>
/// <param name="ContentSnippet">Короткий опис без розмітки (до <see cref="RssReader.SnippetLength"/> символів).</param>
/// <param name="Content">Повний вміст (HTML) або опис.</param>
/// <param name="Creator">Автор.</param>
/// <param name="Categories">Категорії.</param>
/// <param name="Guid">Ідентифікатор запису.</param>
public sealed record RssItem(
    string? Title,
    string? Link,
    string? IsoDate,
    string? PubDate,
    string? ContentSnippet,
    string? Content,
    string? Creator,
    IReadOnlyList<string> Categories,
    string? Guid);

/// <summary>Розібрана стрічка.</summary>
/// <param name="FeedTitle">Назва стрічки.</param>
/// <param name="FeedLink">Сайт стрічки.</param>
/// <param name="Items">Записи, нові першими в порядку стрічки.</param>
public sealed record RssFeed(string? FeedTitle, string? FeedLink, IReadOnlyList<RssItem> Items);

/// <summary>Розбір RSS 2.0, RSS 1.0 (RDF) і Atom. DTD заборонено, зовнішні сутності не завантажуються.</summary>
public static partial class RssReader
{
    /// <summary>Довжина <see cref="RssItem.ContentSnippet"/>.</summary>
    public const int SnippetLength = 500;

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace DublinCore = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Rss10 = "http://purl.org/rss/1.0/";

    /// <summary>Розбирає XML стрічки.</summary>
    /// <param name="xml">Текст стрічки.</param>
    /// <param name="limit">Скільки записів повернути.</param>
    /// <exception cref="ConnectorException">Не XML або не RSS/Atom.</exception>
    public static RssFeed Parse(string xml, int limit)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿', ' ', '\r', '\n', '\t')), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ConnectorException($"Це не коректний XML: {ex.Message}");
        }

        var root = document.Root!;
        if (root.Name == Atom + "feed")
        {
            return new RssFeed(
                Text(root.Element(Atom + "title")),
                AtomLink(root),
                [.. root.Elements(Atom + "entry").Take(limit).Select(AtomItem)]);
        }

        var channel = root.Element("channel") ?? root.Element(Rss10 + "channel");
        if (channel is null)
        {
            throw new ConnectorException($"Це не RSS і не Atom: кореневий елемент «{root.Name.LocalName}».");
        }

        // RSS 2.0: item всередині channel; RSS 1.0 (RDF): поруч з ним.
        var items = channel.Elements("item").Concat(root.Elements(Rss10 + "item")).Take(limit).Select(RssItemOf);
        return new RssFeed(Text(channel.Element("title") ?? channel.Element(Rss10 + "title")), Text(channel.Element("link") ?? channel.Element(Rss10 + "link")), [.. items]);
    }

    private static RssItem RssItemOf(XElement item)
    {
        var title = Text(item.Element("title") ?? item.Element(Rss10 + "title"));
        var description = Text(item.Element("description") ?? item.Element(Rss10 + "description"));
        var full = Text(item.Element(Content + "encoded"));
        var pubDate = Text(item.Element("pubDate")) ?? Text(item.Element(DublinCore + "date"));
        return new RssItem(
            title,
            Text(item.Element("link") ?? item.Element(Rss10 + "link")),
            Iso(pubDate),
            pubDate,
            Snippet(description ?? full),
            full ?? description,
            Text(item.Element(DublinCore + "creator")) ?? Text(item.Element("author")),
            [.. item.Elements("category").Concat(item.Elements(DublinCore + "subject")).Select(Text).OfType<string>()],
            Text(item.Element("guid")));
    }

    private static RssItem AtomItem(XElement entry)
    {
        var published = Text(entry.Element(Atom + "published")) ?? Text(entry.Element(Atom + "updated"));
        var summary = Text(entry.Element(Atom + "summary"));
        var content = Text(entry.Element(Atom + "content"));
        return new RssItem(
            Text(entry.Element(Atom + "title")),
            AtomLink(entry),
            Iso(published),
            published,
            Snippet(summary ?? content),
            content ?? summary,
            Text(entry.Element(Atom + "author")?.Element(Atom + "name")),
            [.. entry.Elements(Atom + "category").Select(c => c.Attribute("term")?.Value).OfType<string>()],
            Text(entry.Element(Atom + "id")));
    }

    private static string? AtomLink(XElement parent)
    {
        var links = parent.Elements(Atom + "link").ToList();
        var link = links.FirstOrDefault(l => (l.Attribute("rel")?.Value ?? "alternate") == "alternate") ?? links.FirstOrDefault();
        return link?.Attribute("href")?.Value;
    }

    private static string? Text(XElement? element) =>
        element is null ? null : element.Value.Trim() is { Length: > 0 } value ? value : null;

    private static string? Snippet(string? html)
    {
        if (html is null)
        {
            return null;
        }

        var text = WhitespaceRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(html, " ")), " ").Trim();
        return text.Length <= SnippetLength ? text : text[..SnippetLength].TrimEnd() + "…";
    }

    /// <summary>Дата RFC 822 (<c>Mon, 02 Jan 2006 15:04:05 +0200</c>, також з GMT/UT/EST…) чи ISO 8601 → ISO 8601 в UTC.</summary>
    private static string? Iso(string? date)
    {
        if (date is null)
        {
            return null;
        }

        var normalized = date.Trim();
        foreach (var (zone, offset) in new[] { (" EET", " +0200"), (" EEST", " +0300"), (" CET", " +0100"), (" CEST", " +0200"), (" MSK", " +0300") })
        {
            if (normalized.EndsWith(zone, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^zone.Length] + offset;
                break;
            }
        }

        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : null;
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

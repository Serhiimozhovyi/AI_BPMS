namespace OpenBP.Connectors.Http.Tests;

public sealed class RssReaderTests
{
    [Fact]
    public void Parses_rss_2_0()
    {
        var feed = RssReader.Parse(FakeWebServer.Rss, 10);

        Assert.Equal("Агро новини", feed.FeedTitle);
        Assert.Equal(2, feed.Items.Count);
        Assert.Equal("g1", feed.Items[0].Guid);
    }

    [Fact]
    public void Parses_atom()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom feed</title>
              <link href="https://a.example/" />
              <entry>
                <title>Запис</title>
                <link rel="alternate" href="https://a.example/1" />
                <id>urn:1</id>
                <updated>2026-10-26T10:00:00Z</updated>
                <summary>Короткий текст</summary>
                <author><name>Олена</name></author>
              </entry>
            </feed>
            """;

        var feed = RssReader.Parse(atom, 10);

        Assert.Equal("Atom feed", feed.FeedTitle);
        var item = Assert.Single(feed.Items);
        Assert.Equal("https://a.example/1", item.Link);
        Assert.StartsWith("2026-10-26T10:00:00", item.IsoDate);
        Assert.Equal("Олена", item.Creator);
        Assert.Equal("Короткий текст", item.ContentSnippet);
    }

    [Fact]
    public void Parses_rss_1_0_and_applies_limit()
    {
        const string rdf = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <channel><title>RDF</title><link>https://r.example/</link></channel>
              <item><title>Один</title><link>https://r.example/1</link><dc:date>2026-10-26T08:00:00Z</dc:date></item>
              <item><title>Два</title><link>https://r.example/2</link></item>
            </rdf:RDF>
            """;

        var feed = RssReader.Parse(rdf, 1);

        Assert.Equal("RDF", feed.FeedTitle);
        Assert.Equal("Один", Assert.Single(feed.Items).Title);
    }

    [Fact]
    public void Rejects_a_document_that_is_not_a_feed()
    {
        Assert.Throws<ConnectorException>(() => RssReader.Parse("<html><body>hi</body></html>", 10));
        Assert.Throws<ConnectorException>(() => RssReader.Parse("не xml", 10));
    }
}

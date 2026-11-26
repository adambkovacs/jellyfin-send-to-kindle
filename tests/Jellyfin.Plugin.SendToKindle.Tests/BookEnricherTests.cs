using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Jellyfin.Plugin.SendToKindle.Enrichment;

namespace Jellyfin.Plugin.SendToKindle.Tests;

public sealed class BookEnricherTests
{
    private const string FixtureIsbn = "9780123456786";
    private const string FixtureTitle = "Sample Book";
    private const string PackagePath = "book/package.opf";
    private const string Author = "Original Author";
    private const string Chapter = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Chapter</title></head><body><p>Fixture text</p></body></html>";
    private const string ApiKey = "fixture-key";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    [Fact]
    public async Task SingleMatchingEditionEnrichesOnlyTheAttachment()
    {
        using var source = new EpubFixture("urn:isbn:0-12345678-9");
        byte[] original = File.ReadAllBytes(source.Path);
        using var originalStream = File.OpenRead(source.Path);
        using var handler = new CatalogHandler();
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client)
            .TryCreateEnrichedAttachmentAsync(originalStream, source.Path, true, ApiKey, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0, result.Position);
        Assert.Equal(original, File.ReadAllBytes(source.Path));
        Assert.Equal(0, originalStream.Position);
        Assert.Equal(1, handler.RequestCount);
        using var epub = new ZipArchive(result, ZipArchiveMode.Read, leaveOpen: true);
        var mimetype = epub.Entries[0];
        Assert.Equal("mimetype", mimetype.FullName);
        Assert.Equal(mimetype.Length, mimetype.CompressedLength);
        using var package = epub.GetEntry(PackagePath)!.Open();
        var document = XDocument.Load(package);
        Assert.Equal(Author, document.Descendants(Dc + "creator").Single().Value);
        Assert.Equal("Sample Press", document.Descendants(Dc + "publisher").Single().Value);
        Assert.Equal("Fiction", document.Descendants(Dc + "subject").Single().Value);
        using var chapter = new StreamReader(epub.GetEntry("book/chapter.xhtml")!.Open());
        Assert.Equal(Chapter, await chapter.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, ApiKey, FixtureIsbn, ".epub")]
    [InlineData(true, "", FixtureIsbn, ".epub")]
    [InlineData(true, ApiKey, FixtureIsbn, ".pdf")]
    [InlineData(true, ApiKey, "9780123456780", ".epub")]
    [InlineData(true, ApiKey, "9780123456786</dc:identifier><dc:identifier>9781234567897", ".epub")]
    public async Task UnsupportedOrUnapprovedInputsStayOffline(bool enabled, string key, string isbn, string extension)
    {
        using var source = new EpubFixture(isbn, extension);
        using var originalStream = File.OpenRead(source.Path);
        using var handler = new CatalogHandler();
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client)
            .TryCreateEnrichedAttachmentAsync(originalStream, source.Path, enabled, key, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, FixtureTitle, FixtureIsbn, 2, Author)]
    [InlineData(HttpStatusCode.OK, "Different Book", FixtureIsbn, 1, Author)]
    [InlineData(HttpStatusCode.OK, FixtureTitle, "9781234567897", 1, Author)]
    [InlineData(HttpStatusCode.OK, FixtureTitle, FixtureIsbn, 1, "Conflicting Author")]
    [InlineData(HttpStatusCode.ServiceUnavailable, FixtureTitle, FixtureIsbn, 1, Author)]
    public async Task AmbiguousUnrelatedOrUnavailableCatalogFallsBack(
        HttpStatusCode status, string title, string isbn, int itemCount, string author)
    {
        using var source = new EpubFixture(FixtureIsbn);
        using var originalStream = File.OpenRead(source.Path);
        using var handler = new CatalogHandler(status, title, isbn, itemCount, author);
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client)
            .TryCreateEnrichedAttachmentAsync(originalStream, source.Path, true, ApiKey, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(0, originalStream.Position);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class CatalogHandler(
        HttpStatusCode status = HttpStatusCode.OK, string title = FixtureTitle, string isbn = FixtureIsbn,
        int itemCount = 1, string author = Author) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("www.googleapis.com", request.RequestUri?.Host);
            Assert.Equal("https", request.RequestUri?.Scheme);
            Assert.Equal("?q=isbn%3A9780123456786&printType=books&maxResults=20", request.RequestUri?.Query);
            Assert.Equal(ApiKey, request.Headers.GetValues("X-Goog-Api-Key").Single());
            Assert.Null(request.Content);
            var item = new
            {
                volumeInfo = new
                {
                    title,
                    industryIdentifiers = new[] { new { type = "ISBN_13", identifier = isbn } },
                    authors = new[] { author, "Catalog Contributor" },
                    publisher = "Sample Press",
                    categories = new[] { "Fiction" },
                },
            };
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    totalItems = itemCount,
                    items = Enumerable.Repeat(item, itemCount).ToArray(),
                }),
            });
        }
    }

    private sealed class EpubFixture : IDisposable
    {
        public EpubFixture(string isbn, string extension = ".epub")
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"kindle-{Guid.NewGuid():N}{extension}");
            using var epub = ZipFile.Open(Path, ZipArchiveMode.Create);
            AddEntry(epub, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            AddEntry(epub, "META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles><rootfile media-type="application/oebps-package+xml" full-path="book/package.opf"/></rootfiles>
                </container>
                """);
            AddEntry(epub, PackagePath, $"""
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="isbn">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:identifier id="isbn">{isbn}</dc:identifier>
                    <dc:title>{FixtureTitle}</dc:title>
                    <dc:creator>{Author}</dc:creator>
                    <dc:language>en</dc:language>
                    <meta property="dcterms:modified">2020-01-01T00:00:00Z</meta>
                  </metadata>
                  <manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            AddEntry(epub, "book/chapter.xhtml", Chapter);
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);

        private static void AddEntry(
            ZipArchive archive, string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path, compression).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}

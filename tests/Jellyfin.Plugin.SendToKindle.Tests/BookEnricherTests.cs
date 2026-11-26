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
    private const string OtherIsbn = "9781234567897";
    private const string PackagePath = "book/package.opf";
    private const string Author = "Original Author";
    private const string Chapter = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Chapter</title></head><body><p>Fixture text</p></body></html>";
    internal const string ApiKey = "fixture-key";
    private const string WorkQuery = "intitle:\"Sample Book\" inauthor:\"Original Author\"";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
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
    [InlineData(false, ApiKey, FixtureIsbn, ".epub", true)]
    [InlineData(true, "", FixtureIsbn, ".epub", true)]
    [InlineData(true, ApiKey, FixtureIsbn, ".pdf", true)]
    [InlineData(true, ApiKey, "9780123456780", ".epub", false)]
    [InlineData(true, ApiKey, "9780123456786</dc:identifier><dc:identifier>9781234567897", ".epub", false)]
    public async Task UnsupportedOrUnapprovedInputsStayOffline(bool enabled, string key, string isbn, string extension, bool workLookup)
    {
        using var source = new EpubFixture(isbn, extension);
        using var originalStream = File.OpenRead(source.Path);
        using var handler = new CatalogHandler();
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client)
            .TryCreateEnrichedAttachmentAsync(originalStream, source.Path, enabled, key, TestContext.Current.CancellationToken, workLookup);

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

    [Theory]
    [InlineData("urn:uuid:book-id")]
    [InlineData(FixtureIsbn + "</dc:identifier><dc:identifier>" + OtherIsbn)]
    public async Task UnambiguousMatchesAcrossBindingsFillOnlyWorkMetadata(string identifiers)
    {
        using var source = new EpubFixture(identifiers, extraMetadata: """
            <dc:creator id="translator">A. Translator</dc:creator>
            <meta refines="#translator" property="role">trl</meta>
            """);
        using var input = File.OpenRead(source.Path);
        byte[] original = File.ReadAllBytes(source.Path);
        input.Position = 11;
        using var handler = new WorkCatalogHandler(query => query == WorkQuery
            ? CatalogPage(
                CatalogVolume(isbn: "9780141182803", categories: ["Fiction", "Hardback"]),
                CatalogVolume(isbn: "9780141182810", categories: ["Fiction", "Paperback"], description: "Paperback description"))
            : throw new InvalidOperationException(query));
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken, workLookupEnabled: true);

        Assert.NotNull(result);
        Assert.Equal(11, input.Position);
        Assert.Equal(original, File.ReadAllBytes(source.Path));
        Assert.Equal([WorkQuery], handler.Queries);
        var metadata = ReadMetadata(result);
        Assert.Equal(FixtureTitle, Assert.Single(metadata.Elements(Dc + "title")).Value);
        Assert.Equal([Author, "A. Translator"], metadata.Elements(Dc + "creator").Select(creator => creator.Value));
        Assert.Equal("trl", Assert.Single(metadata.Elements(Opf + "meta"), meta => (string?)meta.Attribute("property") == "role").Value);
        Assert.Equal("A story & more.", Assert.Single(metadata.Elements(Dc + "description")).Value);
        Assert.Equal("Fiction", Assert.Single(metadata.Elements(Dc + "subject")).Value);
        Assert.Empty(metadata.Elements(Dc + "publisher"));
        Assert.Equal(identifiers.Split("</dc:identifier><dc:identifier>"), metadata.Elements(Dc + "identifier").Select(id => id.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Untitled")]
    public async Task EditionLookupCanFillMissingTitleWithAuthorCorroboration(string? localTitle)
    {
        using var source = new EpubFixture(FixtureIsbn, title: localTitle, extraMetadata: """
            <dc:description>Local description</dc:description>
            <dc:publisher>Local publisher</dc:publisher>
            <dc:subject>Local subject</dc:subject>
            """);
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(query => query == "isbn:" + FixtureIsbn
            ? CatalogPage(CatalogVolume(isbn: FixtureIsbn, subtitle: "A True Story"))
            : throw new InvalidOperationException(query));
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var metadata = ReadMetadata(result);
        Assert.Equal(FixtureTitle + ": A True Story", Assert.Single(metadata.Elements(Dc + "title")).Value);
        Assert.Equal(Author, Assert.Single(metadata.Elements(Dc + "creator")).Value);
        Assert.Equal("Local description", Assert.Single(metadata.Elements(Dc + "description")).Value);
        Assert.Equal("Local publisher", Assert.Single(metadata.Elements(Dc + "publisher")).Value);
        Assert.Equal("Local subject", Assert.Single(metadata.Elements(Dc + "subject")).Value);
    }

    [Theory]
    [InlineData(null, Author, "Contradicting Author", 1)]
    [InlineData(null, null, Author, 0)]
    [InlineData("Untitled", null, Author, 1)]
    [InlineData("Untitled", "Anonymous", Author, 1)]
    public async Task MissingTitleRequiresIndependentEvidence(string? title, string? author, string catalogAuthor, int requests)
    {
        using var source = new EpubFixture(FixtureIsbn, title: title, author: author);
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(_ => CatalogPage(CatalogVolume(isbn: FixtureIsbn, authors: [catalogAuthor])));
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken, workLookupEnabled: true);

        Assert.Null(result);
        Assert.Equal(requests, handler.Queries.Count);
    }

    [Fact]
    public async Task EditionLookupAddsAuthorsAlongsideExplicitEditorAndDoesNotReplaceCredits()
    {
        using var source = new EpubFixture(FixtureIsbn, author: null, extraMetadata: """
            <dc:creator id="editor">Existing Editor</dc:creator>
            <meta refines="#editor" property="role">edt</meta>
            """);
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(_ => CatalogPage(CatalogVolume(isbn: FixtureIsbn, authors: [Author, "Existing Editor", "Coauthor"])));
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var metadata = ReadMetadata(result);
        Assert.Equal([Author, "Coauthor", "Existing Editor"], metadata.Elements(Dc + "creator").Select(creator => creator.Value));
        Assert.Equal("edt", Assert.Single(metadata.Elements(Opf + "meta"), meta => (string?)meta.Attribute("property") == "role").Value);
        Assert.Equal("A story & more.", Assert.Single(metadata.Elements(Dc + "description")).Value);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Unknown Author")]
    public async Task ConfirmedEditionFillsPlaceholderAuthorAndPreservesContributorRoles(string placeholder)
    {
        using var source = new EpubFixture(FixtureIsbn, author: null, extraMetadata: $"""
            <dc:creator id="editor">Existing Editor</dc:creator>
            <dc:creator id="author">{placeholder}</dc:creator>
            <meta refines="#author" property="role">aut</meta>
            <meta refines="#editor" property="role">edt</meta>
            <dc:publisher>Local press</dc:publisher>
            <dc:description>Local description</dc:description>
            <dc:subject>Local subject</dc:subject>
            """);
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(_ => CatalogPage(CatalogVolume(isbn: FixtureIsbn)));
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        var metadata = ReadMetadata(result);
        Assert.Equal([("author", Author), ("editor", "Existing Editor")],
            metadata.Elements(Dc + "creator").Select(creator => ((string?)creator.Attribute("id"), creator.Value)));
        Assert.Equal([("#author", "aut"), ("#editor", "edt")],
            metadata.Elements(Opf + "meta").Where(meta => (string?)meta.Attribute("property") == "role")
                .Select(meta => ((string?)meta.Attribute("refines"), meta.Value)));
    }

    [Theory]
    [InlineData(null, "A Story", "en", Author)]
    [InlineData("Sample Book, Volume Two", null, "en", Author)]
    [InlineData(null, null, "fr", Author)]
    [InlineData(null, null, "en", "Another Author")]
    [InlineData(null, null, null, Author)]
    public async Task WorkLookupRejectsConflictingOrUncorroboratedEvidence(
        string? catalogTitle, string? subtitle, string? language, string author)
    {
        using var source = new EpubFixture("urn:uuid:book-id");
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(_ => CatalogPage(
            CatalogVolume(title: catalogTitle ?? FixtureTitle, subtitle: subtitle, language: language, authors: [author])));
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken, workLookupEnabled: true);

        Assert.Null(result);
        Assert.Equal([WorkQuery], handler.Queries);
    }

    [Fact]
    public async Task ExactSubtitleAndLanguageVariantCorroborateWorkMatch()
    {
        using var source = new EpubFixture("urn:uuid:book-id", language: "en-US", extraMetadata: """
            <dc:title id="subtitle">A True Story</dc:title>
            <meta refines="#subtitle" property="title-type">subtitle</meta>
            """);
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(_ => CatalogPage(CatalogVolume(subtitle: "A True Story")));
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken, workLookupEnabled: true);

        Assert.NotNull(result);
        var metadata = ReadMetadata(result);
        Assert.Equal([FixtureTitle, "A True Story"], metadata.Elements(Dc + "title").Select(title => title.Value));
        Assert.Equal("A story & more.", Assert.Single(metadata.Elements(Dc + "description")).Value);
    }

    [Theory]
    [InlineData("""<meta name="calibre:series_index" content="2" />""")]
    [InlineData("""<meta property="group-position" refines="#collection">2</meta>""")]
    [InlineData("<dc:title>Another Title</dc:title>")]
    public async Task UnresolvedWorkQualifiersStayOffline(string qualifiers)
    {
        using var source = new EpubFixture("urn:uuid:book-id", extraMetadata: qualifiers);
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(_ => CatalogPage(CatalogVolume()));
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken, workLookupEnabled: true);

        Assert.Null(result);
        Assert.Empty(handler.Queries);
    }

    [Fact]
    public async Task MultipleLanguagesWithoutAnEpubLanguageStayUnchanged()
    {
        using var source = new EpubFixture("urn:uuid:book-id", language: null);
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(_ => CatalogPage(
            CatalogVolume(isbn: FixtureIsbn, description: "English text"),
            CatalogVolume(isbn: OtherIsbn, language: "fr", description: "French text")));
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken, workLookupEnabled: true);

        Assert.Null(result);
        Assert.Equal([WorkQuery], handler.Queries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkFallbackAfterIsbnMissRequiresSeparateConsent(bool workLookup)
    {
        using var source = new EpubFixture(FixtureIsbn);
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(query => query == "isbn:" + FixtureIsbn
            ? CatalogPage()
            : CatalogPage(CatalogVolume(isbn: OtherIsbn)));
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken, workLookup);

        if (workLookup)
        {
            Assert.NotNull(result);
            var metadata = ReadMetadata(result);
            Assert.Equal(FixtureIsbn, Assert.Single(metadata.Elements(Dc + "identifier")).Value);
            Assert.Empty(metadata.Elements(Dc + "publisher"));
            Assert.Equal(["isbn:" + FixtureIsbn, WorkQuery], handler.Queries);
        }
        else
        {
            Assert.Null(result);
            Assert.Equal(["isbn:" + FixtureIsbn], handler.Queries);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WorkSearchCanFillGapsAcrossBoundedResultsAfterEditionLookup(int exactCount)
    {
        using var source = new EpubFixture(FixtureIsbn);
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(query => query == "isbn:" + FixtureIsbn
            ? CatalogPage(Enumerable.Repeat(CatalogVolume(isbn: FixtureIsbn, description: string.Empty), exactCount).ToArray())
            : new
            {
                totalItems = 100,
                items = new[]
                {
                    CatalogVolume(authors: ["Another Author"], description: "Different work"),
                    CatalogVolume(isbn: OtherIsbn, description: "Same work"),
                },
            });
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken, workLookupEnabled: true);

        Assert.NotNull(result);
        var metadata = ReadMetadata(result);
        Assert.Equal("Same work", Assert.Single(metadata.Elements(Dc + "description")).Value);
        Assert.Equal(exactCount == 1 ? "Catalog Press" : null, metadata.Element(Dc + "publisher")?.Value);
        Assert.Equal(["isbn:" + FixtureIsbn, WorkQuery], handler.Queries);
    }

    [Theory]
    [InlineData(FixtureTitle, null)]
    [InlineData(null, Author)]
    public async Task EditionLookupCanSupplyTheEvidenceNeededForWorkLookup(string? title, string? author)
    {
        using var source = new EpubFixture(FixtureIsbn, title: title, author: author);
        using var input = File.OpenRead(source.Path);
        using var handler = new WorkCatalogHandler(query => query switch
        {
            "isbn:" + FixtureIsbn => CatalogPage(CatalogVolume(isbn: FixtureIsbn, description: string.Empty)),
            WorkQuery => CatalogPage(CatalogVolume(isbn: OtherIsbn, description: "Alternate binding blurb")),
            _ => throw new InvalidOperationException(query),
        });
        using var client = new HttpClient(handler);

        using var result = await new BookEnricher(client).TryCreateEnrichedAttachmentAsync(
            input, source.Path, true, ApiKey, TestContext.Current.CancellationToken, workLookupEnabled: true);

        Assert.NotNull(result);
        var metadata = ReadMetadata(result);
        Assert.Equal(FixtureTitle, Assert.Single(metadata.Elements(Dc + "title")).Value);
        Assert.Equal(Author, Assert.Single(metadata.Elements(Dc + "creator")).Value);
        Assert.Equal("Alternate binding blurb", Assert.Single(metadata.Elements(Dc + "description")).Value);
        Assert.Equal(["isbn:" + FixtureIsbn, WorkQuery], handler.Queries);
    }

    internal static XElement ReadMetadata(Stream result)
    {
        using var epub = new ZipArchive(result, ZipArchiveMode.Read, leaveOpen: true);
        using var package = epub.GetEntry(PackagePath)!.Open();
        return XDocument.Load(package).Root!.Element(Opf + "metadata")!;
    }

    internal static object CatalogPage(params object[] items) => new { totalItems = items.Length, items };

    internal static object CatalogVolume(
        string title = FixtureTitle, string? subtitle = null, string isbn = OtherIsbn,
        string? language = "en", string[]? authors = null, string description = "<p>A <i>story</i> &amp; more.</p>",
        string[]? categories = null) => new
    {
        volumeInfo = new
        {
            title,
            subtitle,
            authors = authors ?? [Author],
            publisher = "Catalog Press",
            description,
            language,
            categories = categories ?? ["Fiction"],
            industryIdentifiers = new[] { new { type = "ISBN_13", identifier = isbn } },
        },
    };

    internal sealed class WorkCatalogHandler(Func<string, object> response) : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("www.googleapis.com", request.RequestUri?.Host);
            Assert.Equal("https", request.RequestUri?.Scheme);
            Assert.Equal(ApiKey, request.Headers.GetValues("X-Goog-Api-Key").Single());
            Assert.Null(request.Content);
            string query = Uri.UnescapeDataString(request.RequestUri!.Query[3..].Split('&')[0]);
            Queries.Add(query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(response(query)),
            });
        }
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

    internal sealed class EpubFixture : IDisposable
    {
        public EpubFixture(
            string isbn, string extension = ".epub", string? title = FixtureTitle, string? author = Author,
            string? language = "en", string? extraMetadata = null)
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
                    {MakeElement("title", title)}
                    {MakeElement("creator", author)}
                    {MakeElement("language", language)}
                    {extraMetadata}
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

        private static string? MakeElement(string name, string? value) =>
            value is null ? null : $"<dc:{name}>{System.Security.SecurityElement.Escape(value)}</dc:{name}>";

        private static void AddEntry(
            ZipArchive archive, string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path, compression).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}

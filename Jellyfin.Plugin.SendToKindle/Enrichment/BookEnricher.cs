using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.SendToKindle.Enrichment;

internal sealed class BookEnricher(HttpClient httpClient)
{
    private const int MaxBookBytes = 32 * 1024 * 1024;
    private const int MaxMetadataBytes = 1024 * 1024;
    private const string ContainerPath = "META-INF/container.xml";
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Stream?> TryCreateEnrichedAttachmentAsync(
        Stream original,
        string originalPath,
        bool googleBooksEnabled,
        string? googleBooksApiKey,
        CancellationToken cancellationToken = default)
    {
        if (!googleBooksEnabled || string.IsNullOrWhiteSpace(googleBooksApiKey) ||
            !string.Equals(Path.GetExtension(originalPath), ".epub", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!original.CanRead || !original.CanSeek)
        {
            return null;
        }

        var snapshot = new MemoryStream();
        var start = original.Position;
        bool enriched = false;
        try
        {
            var length = original.Length;
            if (length is <= 0 or > MaxBookBytes)
            {
                return null;
            }

            try
            {
                original.Position = 0;
                snapshot.SetLength(length);
                await original.ReadExactlyAsync(snapshot.GetBuffer().AsMemory(0, (int)length), cancellationToken)
                    .ConfigureAwait(false);
                if (original.ReadByte() != -1)
                {
                    return null;
                }
            }
            finally
            {
                original.Position = start;
            }

            string packagePath;
            XDocument package;
            using (var epub = new ZipArchive(snapshot, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (epub.GetEntry("META-INF/signatures.xml") is not null ||
                    epub.GetEntry("META-INF/encryption.xml") is not null ||
                    epub.Entries.Count is 0 or > 20_000 ||
                    epub.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != epub.Entries.Count ||
                    epub.Entries[0].FullName != "mimetype" ||
                    !HasEpubMimeType(epub.Entries[0]))
                {
                    return null;
                }

                var rootFiles = ReadXml(epub.GetEntry(ContainerPath))?.Root?
                    .Element(Container + "rootfiles")?.Elements(Container + "rootfile").ToArray();
                if (rootFiles is not { Length: 1 } ||
                    (string?)rootFiles[0].Attribute("media-type") != "application/oebps-package+xml" ||
                    (string?)rootFiles[0].Attribute("full-path") is not { Length: > 0 } path ||
                    ReadXml(epub.GetEntry(path)) is not { } document)
                {
                    return null;
                }

                packagePath = path;
                package = document;
            }

            var metadata = package.Root?.Name == Opf + "package" ? package.Root.Element(Opf + "metadata") : null;
            if (metadata is null || metadata.Element(Dc + "title")?.Value.Trim() is not { Length: > 0 } title)
            {
                return null;
            }

            var isbns = metadata.Elements(Dc + "identifier")
                .Select(identifier => NormalizeIsbn(identifier.Value))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (isbns.Length != 1)
            {
                return null;
            }

            string[] fields = ["creator", "publisher", "subject"];
            if (fields.All(field => metadata.Element(Dc + field) is not null))
            {
                return null;
            }

            var match = await LookUpBookAsync(isbns[0], title, googleBooksApiKey, cancellationToken).ConfigureAwait(false);
            if (match is null || HasConflictingAuthor(metadata, match.Authors))
            {
                return null;
            }

            bool addedAuthors = FillMissing(metadata, "creator", match.Authors);
            bool addedPublisher = FillMissing(metadata, "publisher", [match.Publisher]);
            bool addedSubjects = FillMissing(metadata, "subject", match.Categories);
            if (!addedAuthors && !addedPublisher && !addedSubjects)
            {
                return null;
            }

            foreach (var modified in metadata.Elements(Opf + "meta")
                .Where(node => (string?)node.Attribute("property") == "dcterms:modified"))
            {
                modified.Value = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            }

            cancellationToken.ThrowIfCancellationRequested();
            snapshot.Position = 0;
            using (var epub = new ZipArchive(snapshot, ZipArchiveMode.Update, leaveOpen: true))
            using (var entry = epub.GetEntry(packagePath)!.Open())
            {
                entry.SetLength(0);
                package.Save(entry, SaveOptions.DisableFormatting);
            }

            snapshot.Position = 0;
            enriched = true;
            return snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or XmlException or HttpRequestException or JsonException or FormatException or
            NotSupportedException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (!enriched)
            {
                snapshot.Dispose();
            }
        }
    }

    private async Task<VolumeInfo?> LookUpBookAsync(
        string isbn,
        string title,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.googleapis.com/books/v1/volumes?q=isbn%3A{isbn}&printType=books&maxResults=20");
        request.Headers.Add("X-Goog-Api-Key", apiKey.Trim());

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await response.Content.LoadIntoBufferAsync(MaxMetadataBytes, timeout.Token).ConfigureAwait(false);
        using var content = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var page = await JsonSerializer.DeserializeAsync<VolumePage>(content, JsonOptions, timeout.Token)
            .ConfigureAwait(false);
        if (page?.Items is not { Length: > 0 } items || page.TotalItems is <= 0 || page.TotalItems > items.Length)
        {
            return null;
        }

        var exact = items.Select(item => item?.VolumeInfo)
            .OfType<VolumeInfo>()
            .Where(volume => volume.IndustryIdentifiers?.Any(identifier =>
                identifier is { Type: "ISBN_10" or "ISBN_13" } &&
                NormalizeIsbn(identifier.Value) == isbn) == true)
            .ToArray();
        return exact.Length == 1 &&
            string.Equals(exact[0].Title?.Trim(), title, StringComparison.OrdinalIgnoreCase)
                ? exact[0]
                : null;
    }

    private static bool HasConflictingAuthor(XElement metadata, string?[]? catalogAuthors)
    {
        if (catalogAuthors is null || !catalogAuthors.Any(author => !string.IsNullOrWhiteSpace(author)))
        {
            return false;
        }

        var authors = metadata.Elements(Dc + "creator").Select(creator => creator.Value.Trim())
            .Where(author => author.Length > 0).ToArray();
        return authors.Length > 0 && !catalogAuthors.Any(author =>
            authors.Contains(author?.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    private static bool FillMissing(XElement metadata, string name, string?[]? values)
    {
        if (values is null || metadata.Element(Dc + name) is not null)
        {
            return false;
        }

        var clean = values.Select(value => value?.Trim())
            .OfType<string>()
            .Where(value => value.Length is > 0 and <= 256)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
        metadata.Add(clean.Select(value => new XElement(Dc + name, value)));
        return clean.Length != 0;
    }

    private static bool HasEpubMimeType(ZipArchiveEntry entry)
    {
        var expected = "application/epub+zip"u8;
        if (entry.Length != expected.Length || entry.CompressedLength != expected.Length)
        {
            return false;
        }

        using var mime = entry.Open();
        Span<byte> bytes = stackalloc byte[expected.Length];
        mime.ReadExactly(bytes);
        return bytes.SequenceEqual(expected);
    }

    private static XDocument? ReadXml(ZipArchiveEntry? entry)
    {
        if (entry is null || entry.Length is <= 0 or > MaxMetadataBytes)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxMetadataBytes,
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string? NormalizeIsbn(string? candidate)
    {
        if (candidate is null || candidate.Length > 64)
        {
            return null;
        }

        candidate = candidate.Trim();
        if (candidate.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[9..];
        }
        else if (candidate.StartsWith("isbn:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[5..];
        }

        var value = new string(candidate.Where(character => character != '-' && !char.IsWhiteSpace(character)).ToArray());
        if (value.Length == 10 && value[..9].All(char.IsAsciiDigit))
        {
            int check = value[9] is 'X' or 'x' ? 10 : value[9] - '0';
            if (check is < 0 or > 10 || (check == 10 && value[9] is not ('X' or 'x')) ||
                (value[..9].Select((digit, index) => (digit - '0') * (10 - index)).Sum() + check) % 11 != 0)
            {
                return null;
            }

            string prefix = "978" + value[..9];
            return prefix + (char)('0' + Isbn13CheckDigit(prefix));
        }

        return value.Length == 13 && value.All(char.IsAsciiDigit) &&
            (value.StartsWith("978", StringComparison.Ordinal) || value.StartsWith("979", StringComparison.Ordinal)) &&
            value[12] - '0' == Isbn13CheckDigit(value[..12])
            ? value
            : null;
    }

    private static int Isbn13CheckDigit(string firstTwelveDigits) =>
        (10 - firstTwelveDigits.Select((digit, index) => (digit - '0') * (index % 2 == 0 ? 1 : 3)).Sum() % 10) % 10;

    private sealed record VolumePage(int TotalItems, Volume?[]? Items);
    private sealed record Volume(VolumeInfo? VolumeInfo);
    private sealed record VolumeInfo(
        string? Title, string?[]? Authors, string? Publisher, string?[]? Categories, Identifier?[]? IndustryIdentifiers);
    private sealed record Identifier(
        string? Type, [property: System.Text.Json.Serialization.JsonPropertyName("identifier")] string? Value);
}

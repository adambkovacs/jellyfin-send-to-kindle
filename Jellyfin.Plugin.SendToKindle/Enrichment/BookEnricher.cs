using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.SendToKindle.Enrichment;

internal sealed class BookEnricher(HttpClient httpClient)
{
    private const int MaxBookBytes = 32 * 1024 * 1024;
    private const int MaxMetadataBytes = 1024 * 1024;
    private const int MaxDescriptionLength = 16_384;
    private const int MaxFieldLength = 256;
    private const string ContainerPath = "META-INF/container.xml";
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex DescriptionBreaks = new(@"</?(?:p|br|div|ul|ol|li)\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex DescriptionFormatting = new(@"</?(?:b|strong|i|em|u|span)\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task<Stream?> TryCreateEnrichedAttachmentAsync(
        Stream original,
        string originalPath,
        bool googleBooksEnabled,
        string? googleBooksApiKey,
        CancellationToken cancellationToken = default,
        bool workLookupEnabled = false)
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
            if (metadata is null)
            {
                return null;
            }

            if (!await TryEnrichMetadataAsync(metadata, googleBooksApiKey, workLookupEnabled, cancellationToken)
                    .ConfigureAwait(false))
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

    private async Task<bool> TryEnrichMetadataAsync(
        XElement metadata, string apiKey, bool workLookupEnabled, CancellationToken cancellationToken)
    {
        var book = ReadBookIdentity(metadata);
        if (book is null)
        {
            return false;
        }

        var isbns = metadata.Elements(Dc + "identifier")
            .Select(identifier => NormalizeIsbn(identifier.Value))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var descriptions = metadata.Elements(Dc + "description");
        var subjects = metadata.Elements(Dc + "subject");
        var publishers = metadata.Elements(Dc + "publisher");
        bool needsWorkFields = !HasValue(descriptions) || !HasValue(subjects);
        bool needsTitle = book.Title is null || IsTitlePlaceholder(book.Title);
        bool needsEditionFields = needsTitle || !HasAuthorCredit(book.AuthorElements) || !HasValue(publishers);
        if (!needsWorkFields && (isbns.Length != 1 || !needsEditionFields))
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        bool changed = false;
        if (isbns.Length == 1)
        {
            // A checksum-valid ISBN without any independent evidence may identify another book.
            if (book.Title is null && book.Authors.Length == 0)
            {
                return false;
            }

            string isbn = isbns[0];
            var results = await SearchAsync($"isbn:{isbn}", apiKey, requireCompleteResults: true, timeout.Token)
                .ConfigureAwait(false);
            if (results is null)
            {
                return false;
            }

            var exact = results.Where(volume => volume.IndustryIdentifiers?.Any(identifier =>
                identifier is { Type: "ISBN_10" or "ISBN_13" } && NormalizeIsbn(identifier.Value) == isbn) == true)
                .ToArray();
            if (exact.Any(volume => !MatchesEdition(book, volume)))
            {
                return false;
            }

            if (exact.Length == 1)
            {
                var match = exact[0];
                if (needsTitle && CorroboratesAuthors(book, ReadCatalogAuthors(match)))
                {
                    var title = FullTitle(match.Title, book.Subtitle is null ? match.Subtitle : null);
                    if (title is not null && !IsTitlePlaceholder(title) && !SameValue(book.Title, title))
                    {
                        changed |= FillMissing(metadata, "title", book.MainTitleElements, CleanValues([title]));
                    }
                }

                // Google Books groups authors and editors together. Keep locally attributed contributors in their roles.
                var contributorNames = ReadIdentityValues(metadata.Elements(Dc + "creator").Except(book.AuthorElements)
                    .Concat(metadata.Elements(Dc + "contributor")));
                changed |= FillMissing(metadata, "creator", book.AuthorElements, CleanValues(match.Authors)
                    .Where(author => !IsAuthorPlaceholder(author) &&
                        !contributorNames.Contains(NormalizeIdentity(author), StringComparer.OrdinalIgnoreCase)));
                changed |= FillMissing(metadata, "publisher", publishers, CleanValues([match.Publisher]));
                changed |= FillMissing(metadata, "description", descriptions,
                    CleanValues([CleanDescription(match.Description)], MaxDescriptionLength));
                changed |= FillMissing(metadata, "subject", subjects, CleanValues(match.Categories));
            }
        }

        if (!workLookupEnabled || HasValue(descriptions) && HasValue(subjects))
        {
            return changed;
        }

        // A corroborated edition match can supply the title or author needed for the work lookup.
        if (changed)
        {
            book = ReadBookIdentity(metadata);
        }

        if (book is not { Title: not null, Authors.Length: > 0, Languages.Length: <= 1, HasVolumePosition: false } ||
            IsTitlePlaceholder(book.Title))
        {
            return changed;
        }

        string query = $"intitle:{SearchPhrase(book.Title)} inauthor:{SearchPhrase(book.Authors[0])}";
        VolumeInfo[]? workResults;
        try
        {
            workResults = await SearchAsync(query, apiKey, requireCompleteResults: false, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return changed;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return changed;
        }

        var matches = workResults?.Where(volume => MatchesWork(book, volume)).ToArray();
        if (matches is not { Length: > 0 } || matches.Select(volume => LanguageCode(volume.Language)).Distinct().Count() != 1)
        {
            return changed;
        }

        // Google relevance order determines which usable blurb to take after the work is corroborated.
        var description = matches.Select(volume => CleanDescription(volume.Description)).FirstOrDefault(value => value is not null);
        changed |= FillMissing(metadata, "description", descriptions, CleanValues([description], MaxDescriptionLength));
        var matchingSubjects = matches.Select(volume => CleanValues(volume.Categories)).ToArray();
        var sharedSubjects = matchingSubjects[0].Where(subject => matchingSubjects.Skip(1).All(categories =>
            categories.Contains(subject, StringComparer.OrdinalIgnoreCase)));
        changed |= FillMissing(metadata, "subject", subjects, sharedSubjects);
        return changed;
    }

    private async Task<VolumeInfo[]?> SearchAsync(
        string query, string apiKey, bool requireCompleteResults, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}&printType=books&maxResults=20");
        request.Headers.Add("X-Goog-Api-Key", apiKey.Trim());

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await response.Content.LoadIntoBufferAsync(MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
        using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var page = await JsonSerializer.DeserializeAsync<VolumePage>(content, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (page is { TotalItems: 0, Items: null or { Length: 0 } })
        {
            return [];
        }

        if (page?.Items is not { Length: > 0 and <= 20 } items ||
            page.TotalItems is not > 0 || (requireCompleteResults && page.TotalItems > items.Length))
        {
            return null;
        }

        var volumes = items.Select(item => item?.VolumeInfo).OfType<VolumeInfo>().ToArray();
        return volumes.Length == items.Length ? volumes : null;
    }

    private static BookIdentity? ReadBookIdentity(XElement metadata)
    {
        var refinements = metadata.Elements(Opf + "meta").ToLookup(node => (
            (string?)node.Attribute("refines"), (string?)node.Attribute("property")));
        IEnumerable<string> Refines(XElement element, string property)
        {
            return (string?)element.Attribute("id") is { Length: > 0 } id
                ? refinements[("#" + id, property)].Select(node => node.Value.Trim())
                : [];
        }

        var titles = metadata.Elements(Dc + "title").ToArray();
        var mainTitleElements = titles.Where(title =>
        {
            var types = Refines(title, "title-type").ToArray();
            return types.Length == 0 || types.Contains("main", StringComparer.Ordinal);
        }).ToArray();
        var subtitleElements = titles.Where(title =>
            Refines(title, "title-type").Contains("subtitle", StringComparer.Ordinal)).ToArray();
        var mainTitles = ReadIdentityValues(mainTitleElements);
        var subtitles = ReadIdentityValues(subtitleElements);
        if (mainTitles.Length > 1 || subtitles.Length > 1 || mainTitleElements.Concat(subtitleElements).Any(
                title => !string.IsNullOrWhiteSpace(title.Value) && NormalizeIdentity(title.Value) is null))
        {
            return null;
        }

        var authorElements = metadata.Elements(Dc + "creator").Where(creator =>
        {
            var roles = Refines(creator, "role").Concat(
                new[] { (string?)creator.Attribute(Opf + "role") }.OfType<string>()).ToArray();
            return roles.Length == 0 || roles.Contains("aut", StringComparer.OrdinalIgnoreCase);
        }).ToArray();
        var authors = ReadIdentityValues(authorElements).Where(IsKnownAuthor).ToArray();
        var languages = metadata.Elements(Dc + "language").Select(language => LanguageCode(language.Value))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        // Google Books does not expose a comparable series position in its Volume metadata.
        bool hasVolumePosition = metadata.Elements(Opf + "meta").Any(node =>
            (string?)node.Attribute("property") == "group-position" ||
            (string?)node.Attribute("name") == "calibre:series_index");
        return new BookIdentity(mainTitles.SingleOrDefault(), subtitles.SingleOrDefault(), authors, languages,
            mainTitleElements, authorElements, hasVolumePosition);
    }

    private static bool MatchesEdition(BookIdentity book, VolumeInfo volume)
    {
        string? title = FullTitle(volume.Title, volume.Subtitle);
        if (title is null || !MatchesLanguage(book, volume))
        {
            return false;
        }

        var authors = ReadCatalogAuthors(volume);
        bool authorsCorroborated = CorroboratesAuthors(book, authors);
        if (book.Authors.Length > 0 && authors.Length > 0 && !authorsCorroborated)
        {
            return false;
        }

        string? localTitle = FullTitle(book.Title, book.Subtitle);
        if (SameValue(localTitle, title) ||
            // Preserve the established ISBN match when the EPUB supplies a main title only.
            (book.Subtitle is null && SameValue(book.Title, volume.Title)))
        {
            return true;
        }

        return (book.Title is null || IsTitlePlaceholder(book.Title)) && authorsCorroborated &&
            (book.Subtitle is null || SameValue(book.Subtitle, volume.Subtitle));
    }

    private static bool CorroboratesAuthors(BookIdentity book, string[] catalogAuthors) =>
        book.Authors.Length > 0 && book.Authors.All(author =>
            catalogAuthors.Contains(author, StringComparer.OrdinalIgnoreCase));

    private static bool MatchesWork(BookIdentity book, VolumeInfo volume)
    {
        return SameValue(FullTitle(book.Title, book.Subtitle), FullTitle(volume.Title, volume.Subtitle)) &&
            MatchesLanguage(book, volume) &&
            (book.Languages.Length == 0 || LanguageCode(volume.Language) is not null) &&
            book.Authors.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(ReadCatalogAuthors(volume));
    }

    private static bool MatchesLanguage(BookIdentity book, VolumeInfo volume)
    {
        return LanguageCode(volume.Language) is not { } language ||
            book.Languages.Length == 0 || book.Languages.Contains(language, StringComparer.Ordinal);
    }

    private static string? LanguageCode(string? value)
    {
        string? code = value?.Trim().Split('-', 2)[0].ToLowerInvariant();
        return code is { Length: >= 2 and <= 3 } && code != "und" && code.All(char.IsAsciiLetter)
            ? code
            : null;
    }

    private static string[] ReadIdentityValues(IEnumerable<XElement> elements) =>
        elements.Select(element => NormalizeIdentity(element.Value)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string[] ReadCatalogAuthors(VolumeInfo volume) =>
        (volume.Authors ?? []).Select(NormalizeIdentity).OfType<string>().Where(IsKnownAuthor).ToArray();

    private static bool IsKnownAuthor(string author) =>
        !IsAuthorPlaceholder(author) &&
        !new[] { "anonymous", "various", "various authors" }.Contains(author, StringComparer.OrdinalIgnoreCase);

    private static bool IsAuthorPlaceholder(string value) =>
        NormalizeIdentity(value) is { } author &&
        (author.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            author.Equals("unknown author", StringComparison.OrdinalIgnoreCase));

    private static bool HasAuthorCredit(IEnumerable<XElement> elements) =>
        elements.Any(element => !string.IsNullOrWhiteSpace(element.Value) && !IsAuthorPlaceholder(element.Value));

    private static bool IsTitlePlaceholder(string value) =>
        string.Equals(NormalizeIdentity(value), "Untitled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(NormalizeIdentity(value), "Unknown Title", StringComparison.OrdinalIgnoreCase);

    private static string? FullTitle(string? title, string? subtitle)
    {
        var main = NormalizeIdentity(title);
        var secondary = NormalizeIdentity(subtitle);
        return main is null || secondary is null || main.EndsWith(": " + secondary, StringComparison.OrdinalIgnoreCase)
            ? main
            : main + ": " + secondary;
    }

    private static bool SameValue(string? first, string? second) =>
        NormalizeIdentity(first) is { } normalized &&
        string.Equals(normalized, NormalizeIdentity(second), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeIdentity(string? value) =>
        value is { Length: > 0 and <= MaxFieldLength } && !string.IsNullOrWhiteSpace(value)
            ? string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Normalize(NormalizationForm.FormC)
            : null;

    private static string SearchPhrase(string value) => "\"" + value.Replace('"', ' ').Replace('\\', ' ') + "\"";

    private static string? CleanDescription(string? value)
    {
        if (value is not { Length: > 0 and <= MaxDescriptionLength })
        {
            return null;
        }

        var plain = WebUtility.HtmlDecode(DescriptionFormatting.Replace(DescriptionBreaks.Replace(value, "\n"), string.Empty));
        return plain.Contains('<') || plain.Contains('>') || string.IsNullOrWhiteSpace(plain)
            ? null
            : plain.Trim();
    }

    private static string[] CleanValues(string?[]? values, int maxLength = MaxFieldLength) =>
        (values ?? []).Select(value => value?.Trim()).OfType<string>()
            .Where(value => value.Length > 0 && value.Length <= maxLength)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();

    private static bool HasValue(IEnumerable<XElement> elements) =>
        elements.Any(element => !string.IsNullOrWhiteSpace(element.Value));

    private static bool FillMissing(XElement metadata, string name, IEnumerable<XElement> elements, IEnumerable<string> values)
    {
        var existing = elements.ToArray();
        var added = values.ToArray();
        bool alreadyPresent = name switch
        {
            "creator" => HasAuthorCredit(existing),
            "title" => existing.Any(element => !string.IsNullOrWhiteSpace(element.Value) && !IsTitlePlaceholder(element.Value)),
            _ => HasValue(existing),
        };
        if (added.Length == 0 || alreadyPresent)
        {
            return false;
        }

        // EPUB reading systems give primary credits to the first creator.
        var firstCreator = name == "creator" ? metadata.Element(Dc + "creator") : null;
        if (existing.FirstOrDefault() is { } replacement)
        {
            replacement.Value = added[0];
            if (firstCreator is not null && replacement != firstCreator)
            {
                replacement.Remove();
                firstCreator.AddBeforeSelf(replacement);
            }

            replacement.AddAfterSelf(added.Skip(1).Select(value => new XElement(Dc + name, value)));
        }
        else if (firstCreator is not null)
        {
            firstCreator.AddBeforeSelf(added.Select(value => new XElement(Dc + name, value)));
        }
        else if (name == "title")
        {
            metadata.AddFirst(new XElement(Dc + name, added[0]));
        }
        else
        {
            metadata.Add(added.Select(value => new XElement(Dc + name, value)));
        }

        return true;
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

    private sealed record BookIdentity(
        string? Title, string? Subtitle, string[] Authors, string[] Languages, XElement[] MainTitleElements,
        XElement[] AuthorElements, bool HasVolumePosition);

    private sealed record VolumePage(int? TotalItems, Volume?[]? Items);
    private sealed record Volume(VolumeInfo? VolumeInfo);
    private sealed record VolumeInfo(
        string? Title, string? Subtitle, string?[]? Authors, string? Publisher, string? Description,
        string?[]? Categories, string? Language, Identifier?[]? IndustryIdentifiers);
    private sealed record Identifier(
        string? Type, [property: System.Text.Json.Serialization.JsonPropertyName("identifier")] string? Value);
}

using System.Reflection;
using System.Xml.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SendToKindle.Delivery;
using Jellyfin.Plugin.SendToKindle.Enrichment;
using Jellyfin.Plugin.SendToKindle.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace Jellyfin.Plugin.SendToKindle.Tests;

public sealed class StandaloneDeliveryTests
{
    [Theory]
    [InlineData(false, false, "unknown")]
    [InlineData(true, false, "smtp_accepted")]
    [InlineData(false, true, "failed")]
    public async Task ReplayKeepsTheOriginalReceiptAndDoesNotSendAgain(
        bool journalTemporarilyUnavailable, bool revokeGrantBeforeSend, string expectedStatus)
    {
        Assert.SkipWhen(journalTemporarilyUnavailable && OperatingSystem.IsWindows(),
            "Windows locks the journal directory while its lease is open.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken cancellationToken = timeout.Token;
        var user = new User("test-user", "test-auth", "test-reset");
        user.SetPermission(PermissionKind.EnableContentDownloading, true);
        var admin = new User("test-admin", "test-auth", "test-reset");
        admin.SetPermission(PermissionKind.EnableContentDownloading, true);
        admin.SetPermission(PermissionKind.IsAdministrator, true);
        var secondReader = new User("second-reader", "test-auth", "test-reset");
        secondReader.SetPermission(PermissionKind.EnableContentDownloading, true);
        var ungranted = new User("ungranted-reader", "test-auth", "test-reset");
        ungranted.SetPermission(PermissionKind.EnableContentDownloading, true);
        Guid bookId = Guid.NewGuid();
        Guid key = Guid.NewGuid();
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string file = Path.Combine(directory, "fixture.epub");
            await File.WriteAllTextAsync(file, "synthetic", cancellationToken);
            bool canSeeBook = true;
            ILibraryManager library = Proxy<ILibraryManager>((_, _) => canSeeBook ? new DownloadableBook { Path = file } : null);
            User[] availableUsers = [user, admin, secondReader, ungranted];
            IUserManager users = Proxy<IUserManager>((_, arguments) =>
                availableUsers.FirstOrDefault(candidate => Equals(candidate.Id, arguments[0])));
            var settings = new SettingsStub(user.Id, secondReader.Id);
            string journalPath = Path.Combine(directory, "journal");
            string displacedJournalPath = Path.Combine(directory, "displaced");
            var smtp = new SyntheticSmtp(
                revokeGrantBeforeSend ? () => settings.OwnerGrantRemoved = true : null,
                journalTemporarilyUnavailable ? () => Directory.Move(journalPath, displacedJournalPath) : null);
            var logger = new PersistenceRetryLogger();
            using var journal = new DeliveryJournal(journalPath);
            using var enrichmentHttp = new HttpClient();
            using var remote = new RemoteDeliveryClient(null, null, new HttpClientHandler());
            var local = new StandaloneDelivery(library, users, settings, smtp, new BookEnricher(enrichmentHttp), journal,
                logger);
            using var router = new DeliveryRouter(settings, users, remote, local, journal, NullLogger<DeliveryRouter>.Instance);
            await router.StartAsync(cancellationToken);
            try
            {
                var options = await router.GetOptionsAsync(user.Id, bookId, cancellationToken);
                Assert.Equal(new DeliveryDevice("personal", "Personal"), Assert.Single(options.Devices));
                Assert.Equal("original", Assert.Single(options.Formats));
                Assert.Equal(options.Devices, (await router.GetOptionsAsync(secondReader.Id, bookId, cancellationToken)).Devices);
                Assert.Equal("admin-only", Assert.Single((await router.GetOptionsAsync(admin.Id, bookId, cancellationToken)).Devices).Id);
                Assert.Empty((await router.GetOptionsAsync(ungranted.Id, bookId, cancellationToken)).Devices);

                var deniedRecipient = await Assert.ThrowsAsync<DeliveryException>(() =>
                    router.SubmitAsync(ungranted.Id, bookId, "personal", "original", key, cancellationToken));
                Assert.Equal("unknown_device", deniedRecipient.Error);
                Assert.Null(journal.FindByKey(ungranted.Id, key));

                var submission = await router.SubmitAsync(user.Id, bookId, "personal", "original", key, cancellationToken);
                Assert.False(submission.IsReplay);
                Guid jobId = Guid.Parse(submission.Receipt.JobId);
                if (journalTemporarilyUnavailable)
                {
                    await logger.RetryStarted.Task.WaitAsync(cancellationToken);
                    Assert.Equal("submitting", (await router.GetStatusAsync(user.Id, jobId, cancellationToken)).Status);
                    Directory.Move(displacedJournalPath, journalPath);
                }

                DeliveryReceipt receipt = await router.GetStatusAsync(user.Id, jobId, cancellationToken);
                while (receipt.Status != expectedStatus)
                {
                    await Task.Delay(10, cancellationToken);
                    receipt = await router.GetStatusAsync(user.Id, jobId, cancellationToken);
                }

                Assert.Equal(revokeGrantBeforeSend ? 0 : 1, smtp.Attempts);
                var privateJob = await Assert.ThrowsAsync<DeliveryException>(async () =>
                    await router.GetStatusAsync(secondReader.Id, jobId, cancellationToken));
                Assert.Equal(404, privateJob.StatusCode);

                settings.Mode = DeliveryMode.Service;
                await AssertServiceForbidden(router, journal, user, bookId, cancellationToken);

                Assert.Equal(receipt, await router.GetStatusAsync(user.Id, jobId, cancellationToken));
                var replay = await router.SubmitAsync(user.Id, bookId, "personal", "original", key, cancellationToken);
                Assert.True(replay.IsReplay);
                Assert.Equal(receipt, replay.Receipt);
                Assert.Equal(revokeGrantBeforeSend ? 0 : 1, smtp.Attempts);

                canSeeBook = false;
                var lostAccess = await Assert.ThrowsAsync<DeliveryException>(async () =>
                    await router.GetStatusAsync(user.Id, jobId, cancellationToken));
                Assert.Equal(404, lostAccess.StatusCode);
            }
            finally
            {
                await router.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliverySendsTheOriginalOrEnrichedEpubAccordingToWorkLookupConsent(bool workLookupEnabled)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var fixture = new BookEnricherTests.EpubFixture("urn:uuid:delivery-fixture");
        byte[] original = await File.ReadAllBytesAsync(fixture.Path, cancellation);
        var user = new User("test-user", "test-auth", "test-reset");
        user.SetPermission(PermissionKind.EnableContentDownloading, true);
        ILibraryManager library = Proxy<ILibraryManager>((_, _) => new DownloadableBook { Path = fixture.Path });
        IUserManager users = Proxy<IUserManager>((_, _) => user);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("kindle-delivery-enrichment-");
        try
        {
            var settings = new KindleSettingsStore(directory.FullName, _ => null);
            Assert.Null(settings.Save(new KindleSettings
            {
                Mode = DeliveryMode.Smtp,
                Smtp = new SmtpSettings { Host = "smtp.example.invalid", Sender = "sender@example.invalid" },
                GoogleBooksEnabled = true,
                GoogleBooksWorkLookupEnabled = workLookupEnabled,
                GoogleBooksApiKey = BookEnricherTests.ApiKey,
                Recipients = [new RecipientSettings
                {
                    Label = "Reader", Address = "reader@example.invalid", AllowedUserIds = [user.Id],
                }],
            }, false, false));
            var catalog = BookEnricherTests.CatalogPage(BookEnricherTests.CatalogVolume());
            using var http = new BookEnricherTests.WorkCatalogHandler(_ => catalog);
            using var client = new HttpClient(http);
            using var journal = new DeliveryJournal(Path.Combine(directory.FullName, "journal"));
            var smtp = new CapturingSmtp();
            var delivery = new StandaloneDelivery(library, users, settings, smtp, new BookEnricher(client), journal,
                NullLogger<StandaloneDelivery>.Instance);
            string deviceId = Assert.Single(settings.Read().Recipients).Id;
            var request = new DeliveryRequest(user.Id, Guid.NewGuid(), deviceId, "original", Guid.NewGuid());
            var (entry, _) = journal.Reserve(request, DeliveryMode.Smtp);

            await delivery.ProcessAsync(entry, cancellation);

            Assert.Equal(DeliveryStatus.SmtpAccepted, journal.FindByKey(user.Id, request.Key)?.Status);
            Assert.Equal("reader@example.invalid", smtp.Recipient);
            Assert.Equal(original, await File.ReadAllBytesAsync(fixture.Path, cancellation));
            Assert.Equal(workLookupEnabled ? 1 : 0, http.Queries.Count);
            using var messageBytes = new MemoryStream(Assert.IsType<byte[]>(smtp.Message));
            var message = await MimeMessage.LoadAsync(messageBytes, cancellation);
            var attachment = Assert.IsType<MimePart>(Assert.Single(message.Attachments));
            Assert.Equal("application/epub+zip", attachment.ContentType.MimeType);
            Assert.Equal(Path.GetFileName(fixture.Path), attachment.FileName);
            using var epubBytes = new MemoryStream();
            Assert.NotNull(attachment.Content);
            await attachment.Content.DecodeToAsync(epubBytes, cancellation);
            if (!workLookupEnabled)
            {
                Assert.Equal(original, epubBytes.ToArray());
                return;
            }

            epubBytes.Position = 0;
            var metadata = BookEnricherTests.ReadMetadata(epubBytes);
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            Assert.Equal("A story & more.", Assert.Single(metadata.Elements(dc + "description")).Value);
            Assert.Equal("urn:uuid:delivery-fixture", Assert.Single(metadata.Elements(dc + "identifier")).Value);
            Assert.Empty(metadata.Elements(dc + "publisher"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task AssertServiceForbidden(
        DeliveryRouter router, DeliveryJournal journal, User user, Guid bookId, CancellationToken cancellationToken)
    {
        Guid freshKey = Guid.NewGuid();
        Assert.Equal(403, (await Assert.ThrowsAsync<DeliveryException>(async () =>
            await router.GetOptionsAsync(user.Id, bookId, cancellationToken))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<DeliveryException>(() =>
            router.SubmitAsync(user.Id, bookId, "personal", "original", freshKey, cancellationToken))).StatusCode);
        Assert.Null(journal.FindByKey(user.Id, freshKey));
        Assert.Equal(403, (await Assert.ThrowsAsync<DeliveryException>(async () =>
            await router.GetStatusAsync(user.Id, Guid.NewGuid(), cancellationToken))).StatusCode);

        var remoteRequest = new DeliveryRequest(user.Id, bookId, "personal", "original", Guid.NewGuid());
        var (entry, _) = journal.Reserve(remoteRequest, DeliveryMode.Service);
        Guid remoteJobId = Guid.NewGuid();
        journal.SetRemoteJobId(entry, remoteJobId);
        Assert.Equal(403, (await Assert.ThrowsAsync<DeliveryException>(() =>
            router.SubmitAsync(user.Id, bookId, "personal", "original", remoteRequest.Key, cancellationToken))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<DeliveryException>(async () =>
            await router.GetStatusAsync(user.Id, remoteJobId, cancellationToken))).StatusCode);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        T proxy = DispatchProxy.Create<T, SendToKindleControllerTests.StubProxy>();
        ((SendToKindleControllerTests.StubProxy)(object)proxy).Handler = invoke;
        return proxy;
    }

    private sealed class DownloadableBook : Book
    {
        public override bool CanDownload() => true;
    }

    private sealed class SettingsStub(Guid owner, Guid secondReader) : IKindleSettingsStore
    {
        public DeliveryMode Mode { get; set; } = DeliveryMode.Smtp;
        public bool OwnerGrantRemoved { get; set; }
        public bool TracksSmtpEnvironmentPassword => false;
        public bool TracksGoogleEnvironmentKey => false;

        public KindleSettings Read() => new()
        {
            Mode = Mode,
            Recipients =
            [
                new()
                {
                    Id = "personal",
                    Label = "Personal",
                    AllowedUserIds = OwnerGrantRemoved ? [secondReader] : [owner, secondReader],
                    Address = "reader@example.invalid",
                },
                new() { Id = "admin-only", Label = "Admin only", Address = "admin@example.invalid" },
                new()
                {
                    Id = "disabled",
                    Label = "Disabled",
                    Enabled = false,
                    AllowedUserIds = [owner],
                    Address = "disabled@example.invalid",
                },
            ],
        };

        public string? Save(KindleSettings requested, bool clearPassword, bool clearGoogleKey) => throw new NotSupportedException();
    }

    private sealed class SyntheticSmtp(Action? beforeSubmitting, Action? displaceJournal) : IStandaloneSmtpTransport
    {
        public int Attempts { get; private set; }

        public Task<bool> SendAsync(
            SmtpSettings settings, string recipientAddress, MimeMessage message, Action onSubmitting, CancellationToken cancellationToken)
        {
            beforeSubmitting?.Invoke();
            onSubmitting();
            Attempts++;
            if (displaceJournal is null)
            {
                throw new IOException("Synthetic connection drop.");
            }

            displaceJournal();
            return Task.FromResult(true);
        }
    }

    private sealed class CapturingSmtp : IStandaloneSmtpTransport
    {
        public string? Recipient { get; private set; }
        public byte[]? Message { get; private set; }

        public async Task<bool> SendAsync(
            SmtpSettings settings, string recipientAddress, MimeMessage message, Action onSubmitting, CancellationToken cancellationToken)
        {
            onSubmitting();
            Recipient = recipientAddress;
            using var messageBytes = new MemoryStream();
            await message.WriteToAsync(messageBytes, cancellationToken);
            Message = messageBytes.ToArray();
            return true;
        }
    }

    private sealed class PersistenceRetryLogger : ILogger<StandaloneDelivery>
    {
        public TaskCompletionSource RetryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("Retrying the journal write.", StringComparison.Ordinal))
            {
                RetryStarted.TrySetResult();
            }
        }
    }
}

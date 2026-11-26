using System.Reflection;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SendToKindle.Delivery;
using Jellyfin.Plugin.SendToKindle.Enrichment;
using Jellyfin.Plugin.SendToKindle.Settings;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace Jellyfin.Plugin.SendToKindle.Tests;

public sealed class StandaloneDeliveryTests
{
    [Fact]
    public async Task LostResponseIsUnknownAndReplayStaysLocalAfterModeChange()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        CancellationToken cancellationToken = timeout.Token;
        var user = new User("test-user", "test-auth", "test-reset");
        user.SetPermission(PermissionKind.IsAdministrator, true);
        user.SetPermission(PermissionKind.EnableContentDownloading, true);
        Guid bookId = Guid.NewGuid();
        Guid key = Guid.NewGuid();
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string file = Path.Combine(directory, "fixture.epub");
            await File.WriteAllTextAsync(file, "synthetic", cancellationToken);
            bool canSeeBook = true;
            ILibraryManager library = Proxy<ILibraryManager>((_, _) => canSeeBook ? new DownloadableBook { Path = file } : null);
            IUserManager users = Proxy<IUserManager>((_, arguments) =>
                Equals(user.Id, arguments?[0]) ? user : null);
            var settings = new SettingsStub(user.Id);
            var smtp = new LosingSmtpResponse();
            using var journal = new DeliveryJournal(Path.Combine(directory, "journal"));
            using var enrichmentHttp = new HttpClient();
            using var remote = new RemoteDeliveryClient(null, null, new HttpClientHandler());
            var local = new StandaloneDelivery(library, users, settings, smtp, new BookEnricher(enrichmentHttp), journal,
                NullLogger<StandaloneDelivery>.Instance);
            using var router = new DeliveryRouter(settings, remote, local, journal, NullLogger<DeliveryRouter>.Instance);
            await router.StartAsync(cancellationToken);
            try
            {
                var options = await router.GetOptionsAsync(user.Id, bookId, cancellationToken);
                Assert.Equal(new DeliveryDevice("personal", "Personal"), Assert.Single(options.Devices));
                Assert.Equal("original", Assert.Single(options.Formats));

                var submission = await router.SubmitAsync(user.Id, bookId, "personal", "original", key, cancellationToken);
                Assert.False(submission.IsReplay);
                Guid jobId = Guid.Parse(submission.Receipt.JobId);
                DeliveryReceipt receipt = await router.GetStatusAsync(user.Id, jobId, cancellationToken);
                while (receipt.Status != "unknown")
                {
                    await Task.Delay(10, cancellationToken);
                    receipt = await router.GetStatusAsync(user.Id, jobId, cancellationToken);
                }

                Assert.Equal(1, smtp.Attempts);
                var privateJob = await Assert.ThrowsAsync<DeliveryException>(async () =>
                    await router.GetStatusAsync(Guid.NewGuid(), jobId, cancellationToken));
                Assert.Equal(404, privateJob.StatusCode);

                settings.Mode = DeliveryMode.Service;
                var replay = await router.SubmitAsync(user.Id, bookId, "personal", "original", key, cancellationToken);
                Assert.True(replay.IsReplay);
                Assert.Equal(receipt, replay.Receipt);
                Assert.Equal(1, smtp.Attempts);

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

    private sealed class SettingsStub(Guid owner) : IKindleSettingsStore
    {
        public DeliveryMode Mode { get; set; } = DeliveryMode.Smtp;
        public bool TracksSmtpEnvironmentPassword => false;
        public bool TracksGoogleEnvironmentKey => false;

        public KindleSettings Read() => new()
        {
            Mode = Mode,
            Recipients =
            [
                new() { Id = "personal", Label = "Personal", JellyfinUserId = owner, Address = "reader@example.invalid" },
                new() { Id = "other", Label = "Other", JellyfinUserId = Guid.Empty, Address = "other@example.invalid" },
                new() { Id = "disabled", Label = "Disabled", Enabled = false, Address = "disabled@example.invalid" },
            ],
        };

        public string? Save(KindleSettings requested, bool clearPassword, bool clearGoogleKey) => throw new NotSupportedException();
    }

    private sealed class LosingSmtpResponse : IStandaloneSmtpTransport
    {
        public int Attempts { get; private set; }

        public Task<bool> SendAsync(
            SmtpSettings settings, string recipientAddress, MimeMessage message, Action onSubmitting, CancellationToken cancellationToken)
        {
            onSubmitting();
            Attempts++;
            throw new IOException("Synthetic connection drop.");
        }
    }
}

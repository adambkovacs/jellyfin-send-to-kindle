using System.Text.Json;
using System.Threading.Channels;
using Jellyfin.Plugin.SendToKindle.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SendToKindle.Delivery;

internal sealed class DeliveryRouter(
    IKindleSettingsStore settingsStore,
    RemoteDeliveryClient remote,
    StandaloneDelivery standalone,
    DeliveryJournal journal,
    ILogger<DeliveryRouter> logger) : BackgroundService, IDeliveryClient
{
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    public Task<DeliveryOptions> GetOptionsAsync(Guid userId, Guid bookId, CancellationToken cancellationToken)
    {
        var settings = ReadSettings();
        return settings.Mode == DeliveryMode.Smtp
            ? Task.FromResult(standalone.Options(settings, userId, bookId))
            : remote.GetOptionsAsync(userId, bookId, cancellationToken);
    }

    public async Task<DeliverySubmission> SubmitAsync(
        Guid userId, Guid bookId, string deviceId, string format, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        if (deviceId.Length > 256 || format.Length > 64)
        {
            throw new DeliveryException(400, "invalid_request");
        }

        var request = new DeliveryRequest(userId, bookId, deviceId, format, idempotencyKey);
        var pinned = ReadJournal(() => journal.FindByKey(userId, idempotencyKey));
        var mode = pinned?.Mode;
        if (mode is null)
        {
            var settings = ReadSettings();
            mode = settings.Mode;
            if (mode == DeliveryMode.Smtp)
            {
                standalone.ValidateRequest(settings, request);
            }
        }

        var (entry, isReplay) = ReadJournal(() => journal.Reserve(request, mode.Value));
        if (entry.Mode == DeliveryMode.Smtp)
        {
            // Replays return their original receipt even if that device is now disabled.
            if (entry.Status == DeliveryStatus.Queued)
            {
                _wake.Writer.TryWrite(true);
            }

            return new DeliverySubmission(entry.Receipt, isReplay);
        }

        // A retry is always sent to the originally-selected provider, with the original key.
        var submission = await remote.SubmitAsync(userId, bookId, deviceId, format, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        ReadJournal(() =>
        {
            journal.SetRemoteJobId(entry, Guid.Parse(submission.Receipt.JobId));
            return true;
        });
        return submission;
    }

    public Task<DeliveryReceipt> GetStatusAsync(Guid userId, Guid jobId, CancellationToken cancellationToken)
    {
        var entry = ReadJournal(() => journal.FindByJobId(userId, jobId));
        if (entry?.Mode == DeliveryMode.Smtp)
        {
            standalone.VerifyBookAccess(userId, entry.Request.BookId);
            return Task.FromResult(entry.Receipt);
        }

        if (entry?.Mode == DeliveryMode.Service || ReadSettings().Mode == DeliveryMode.Service)
        {
            return remote.GetStatusAsync(userId, jobId, cancellationToken);
        }

        throw new DeliveryException(404, "unavailable");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _wake.Writer.TryWrite(true);
        await foreach (bool _ in _wake.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                foreach (DeliveryEntry entry in journal.GetQueued())
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await standalone.ProcessAsync(entry, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsStorageError(exception))
            {
                logger.LogWarning("The Send to Kindle delivery journal is unavailable.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
                _wake.Writer.TryWrite(true);
            }
        }
    }

    private KindleSettings ReadSettings() => ReadJournal(settingsStore.Read);

    private static T ReadJournal<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (IsStorageError(exception))
        {
            throw new DeliveryException(503, "unavailable");
        }
    }

    private static bool IsStorageError(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException;
}

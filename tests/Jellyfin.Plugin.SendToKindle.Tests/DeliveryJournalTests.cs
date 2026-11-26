using Jellyfin.Plugin.SendToKindle.Delivery;
using Jellyfin.Plugin.SendToKindle.Settings;

namespace Jellyfin.Plugin.SendToKindle.Tests;

public sealed class DeliveryJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void PinIsOwnerScopedAndRejectsConflictingReplayAcrossModes()
    {
        using DeliveryJournal journal = new(_directory);
        DeliveryRequest first = NewRequest();
        DeliveryRequest secondOwner = first with { UserId = Guid.NewGuid() };

        var (initial, firstReplay) = journal.Reserve(first, DeliveryMode.Smtp);
        var (replay, isReplay) = journal.Reserve(first, DeliveryMode.Service);
        var (other, otherReplay) = journal.Reserve(secondOwner, DeliveryMode.Service);

        Assert.False(firstReplay);
        Assert.True(isReplay);
        Assert.False(otherReplay);
        Assert.Equal(initial, replay);
        Assert.Equal(DeliveryMode.Service, other.Mode);
        Assert.Null(journal.FindByJobId(secondOwner.UserId, initial.JobId));
        var conflict = Assert.Throws<DeliveryException>(() =>
            journal.Reserve(first with { DeviceId = "different" }, DeliveryMode.Service));
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public void RestartRetainsTerminalReceiptsAndDoesNotRetrySubmittingJobs()
    {
        DeliveryRequest accepted = NewRequest();
        DeliveryRequest submitting = NewRequest();
        DeliveryRequest queued = NewRequest();
        Guid acceptedJobId;

        using (DeliveryJournal journal = new(_directory))
        {
            var (entry, _) = journal.Reserve(accepted, DeliveryMode.Smtp);
            acceptedJobId = entry.JobId;
            journal.SetStatus(entry, DeliveryStatus.Submitting);
            journal.SetStatus(entry, DeliveryStatus.SmtpAccepted);
            (entry, _) = journal.Reserve(submitting, DeliveryMode.Smtp);
            journal.SetStatus(entry, DeliveryStatus.Submitting);
            journal.Reserve(queued, DeliveryMode.Smtp);
        }

        using DeliveryJournal recovered = new(_directory);
        Assert.Equal(DeliveryStatus.SmtpAccepted, recovered.FindByJobId(accepted.UserId, acceptedJobId)?.Status);
        Assert.Equal(DeliveryStatus.Unknown, recovered.FindByKey(submitting.UserId, submitting.Key)?.Status);
        Assert.Equal(queued, Assert.Single(recovered.GetQueued()).Request);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static DeliveryRequest NewRequest() => new(Guid.NewGuid(), Guid.NewGuid(), "reader", "original", Guid.NewGuid());
}

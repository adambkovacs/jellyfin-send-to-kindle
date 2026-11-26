using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SendToKindle.Settings;

namespace Jellyfin.Plugin.SendToKindle.Delivery;

internal sealed record DeliveryRequest(Guid UserId, Guid BookId, string DeviceId, string Format, Guid Key);

internal enum DeliveryStatus { Queued, Submitting, SmtpAccepted, Failed, Unknown }

internal sealed record DeliveryEntry(
    DeliveryRequest Request,
    DeliveryMode Mode,
    Guid JobId,
    DateTimeOffset CreatedAt,
    DeliveryStatus Status,
    Guid? RemoteJobId = null)
{
    [JsonIgnore]
    public DeliveryReceipt Receipt => new(JobId.ToString("D"), Status switch
    {
        DeliveryStatus.Queued => "queued",
        DeliveryStatus.Submitting => "submitting",
        DeliveryStatus.SmtpAccepted => "smtp_accepted",
        DeliveryStatus.Failed => "failed",
        DeliveryStatus.Unknown => "unknown",
        _ => throw new InvalidOperationException("Invalid delivery status."),
    });
}

internal sealed class DeliveryJournal(string directory) : IDisposable
{
    private const int QueueCapacity = 50;
    private const UnixFileMode FileModeOwner = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid UserId, Guid Key), DeliveryEntry> _requests = [];
    private readonly Dictionary<(Guid UserId, Guid JobId), (Guid UserId, Guid Key)> _jobs = [];
    private FileStream? _lease;

    public DeliveryEntry? FindByKey(Guid userId, Guid key)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _requests.GetValueOrDefault((userId, key));
        }
    }

    public DeliveryEntry? FindByJobId(Guid userId, Guid jobId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _jobs.TryGetValue((userId, jobId), out var requestKey) ? _requests[requestKey] : null;
        }
    }

    public (DeliveryEntry Entry, bool Replay) Reserve(DeliveryRequest request, DeliveryMode mode)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_requests.TryGetValue((request.UserId, request.Key), out DeliveryEntry? existing))
            {
                if (existing.Request != request)
                {
                    throw new DeliveryException(409, "key_conflict");
                }

                return (existing, true);
            }

            if (mode == DeliveryMode.Smtp && _requests.Values.Count(IsPending) >= QueueCapacity)
            {
                throw new DeliveryException(503, "unavailable");
            }

            var entry = new DeliveryEntry(request, mode, Guid.NewGuid(), DateTimeOffset.UtcNow, DeliveryStatus.Queued);
            Persist(entry);
            return (entry, false);
        }
    }

    public IReadOnlyList<DeliveryEntry> GetQueued()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _requests.Values
                .Where(entry => entry.Mode == DeliveryMode.Smtp && entry.Status == DeliveryStatus.Queued)
                .OrderBy(entry => entry.CreatedAt)
                .ToArray();
        }
    }

    public void SetStatus(DeliveryEntry entry, DeliveryStatus status)
    {
        lock (_gate)
        {
            EnsureLoaded();
            DeliveryEntry current = _requests[(entry.Request.UserId, entry.Request.Key)];
            bool valid = current.Status switch
            {
                DeliveryStatus.Queued => status is DeliveryStatus.Submitting or DeliveryStatus.Failed,
                DeliveryStatus.Submitting => status is DeliveryStatus.SmtpAccepted or DeliveryStatus.Failed or DeliveryStatus.Unknown,
                _ => false,
            };
            if (current.Mode != DeliveryMode.Smtp || !valid)
            {
                throw new InvalidOperationException("Invalid delivery status transition.");
            }

            Persist(current with { Status = status });
        }
    }

    public void SetRemoteJobId(DeliveryEntry entry, Guid jobId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            DeliveryEntry current = _requests[(entry.Request.UserId, entry.Request.Key)];
            if (current.RemoteJobId == jobId)
            {
                return;
            }

            if (current.Mode != DeliveryMode.Service || current.RemoteJobId is not null
                || _jobs.ContainsKey((entry.Request.UserId, jobId)))
            {
                throw new DeliveryException(503, "unavailable");
            }

            Persist(current with { RemoteJobId = jobId });
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _lease?.Dispose();
        }
    }

    private void EnsureLoaded()
    {
        if (_lease is not null)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, FileModeOwner | UnixFileMode.UserExecute);
        }

        // A Jellyfin data directory belongs to one server. Exclude another process writing the same journal.
        FileStream startupLease = OpenPrivateFile(Path.Combine(directory, ".lock"), FileMode.OpenOrCreate);
        try
        {
            LoadFiles();
            _lease = startupLease;
        }
        catch
        {
            startupLease.Dispose();
            throw;
        }
    }

    private void LoadFiles()
    {
        _requests.Clear();
        _jobs.Clear();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            using FileStream source = File.OpenRead(path);
            var entry = JsonSerializer.Deserialize<DeliveryEntry>(source);
            if (entry is null || entry.Request is null || entry.JobId == Guid.Empty || entry.Request.UserId == Guid.Empty
                || entry.Request.BookId == Guid.Empty || entry.Request.Key == Guid.Empty
                || !Enum.IsDefined(entry.Status) || !Enum.IsDefined(entry.Mode)
                || !StringComparer.Ordinal.Equals(Path.GetFileName(path), FileName(entry.Request))
                || _requests.ContainsKey((entry.Request.UserId, entry.Request.Key))
                || _jobs.ContainsKey((entry.Request.UserId, entry.JobId))
                || (entry.RemoteJobId is Guid remoteJobId && _jobs.ContainsKey((entry.Request.UserId, remoteJobId))))
            {
                throw new InvalidDataException("Invalid delivery journal entry.");
            }

            Remember(entry);
        }

        foreach (DeliveryEntry entry in _requests.Values.Where(entry =>
                     entry.Mode == DeliveryMode.Smtp && entry.Status == DeliveryStatus.Submitting).ToArray())
        {
            Persist(entry with { Status = DeliveryStatus.Unknown });
        }

    }

    private void Persist(DeliveryEntry entry)
    {
        string path = Path.Combine(directory, FileName(entry.Request));
        string temporaryPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream output = OpenPrivateFile(temporaryPath, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(output, entry);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            Remember(entry);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Retain the write error for the caller.
            }

            throw;
        }
    }

    private void Remember(DeliveryEntry entry)
    {
        var key = (entry.Request.UserId, entry.Request.Key);
        _requests[key] = entry;
        if (entry.Mode == DeliveryMode.Smtp || entry.RemoteJobId is not null)
        {
            _jobs[(entry.Request.UserId, entry.RemoteJobId ?? entry.JobId)] = key;
        }
    }

    private static FileStream OpenPrivateFile(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileModeOwner;
        }

        return new FileStream(path, options);
    }

    private static string FileName(DeliveryRequest request) => $"{request.UserId:N}-{request.Key:N}.json";

    private static bool IsPending(DeliveryEntry entry) =>
        entry.Mode == DeliveryMode.Smtp && entry.Status is DeliveryStatus.Queued or DeliveryStatus.Submitting;
}

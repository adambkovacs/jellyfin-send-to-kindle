using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SendToKindle.Settings;

public enum DeliveryMode
{
    Service,
    Smtp,
}

public enum SmtpTransport
{
    StartTls,
    Tls,
}

public sealed class SmtpSettings
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 465;
    public SmtpTransport Transport { get; init; } = SmtpTransport.Tls;
    public string Sender { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public long MaxAttachmentBytes { get; init; } = 20 * 1024 * 1024;
}

public sealed class RecipientSettings
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<Guid>? AllowedUserIds { get; init; }

    // Read settings written before recipients could grant access to more than one user.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? JellyfinUserId { get; init; }

    internal IReadOnlyList<Guid> GrantedUsers =>
        AllowedUserIds ?? (JellyfinUserId is Guid legacyUserId ? [legacyUserId] : []);
}

public sealed class KindleSettings
{
    public Guid Revision { get; init; }
    public DeliveryMode Mode { get; init; }
    public SmtpSettings Smtp { get; init; } = new();
    public IReadOnlyList<RecipientSettings> Recipients { get; init; } = [];
    public bool GoogleBooksEnabled { get; init; }
    public string? GoogleBooksApiKey { get; init; }
}

public interface IKindleSettingsStore
{
    KindleSettings Read();
    bool TracksSmtpEnvironmentPassword { get; }
    bool TracksGoogleEnvironmentKey { get; }
    string? Save(KindleSettings requested, bool clearPassword, bool clearGoogleKey);
}

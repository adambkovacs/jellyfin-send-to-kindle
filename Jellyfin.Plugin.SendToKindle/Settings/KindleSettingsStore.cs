using System.Net.Mail;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.SendToKindle.Settings;

public sealed class KindleSettingsStore : IKindleSettingsStore
{
    private const string PasswordVariable = "JELLYFIN_SEND_TO_KINDLE_SMTP_PASSWORD";
    private const string GoogleKeyVariable = "JELLYFIN_SEND_TO_KINDLE_GOOGLE_BOOKS_API_KEY";
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string _settingsPath;
    private readonly object _lock = new();
    private readonly Func<string, string?> _environment;

    public KindleSettingsStore(IApplicationPaths paths)
        : this(paths.PluginConfigurationsPath, Environment.GetEnvironmentVariable)
    {
    }

    internal KindleSettingsStore(string configurationPath, Func<string, string?> environment)
    {
        _settingsPath = Path.Combine(configurationPath, "send-to-kindle-private-settings.json");
        _environment = environment;
    }

    public KindleSettings Read()
    {
        lock (_lock)
        {
            KindleSettings saved = ReadSaved();
            return Copy(saved, GetSmtpEnvironmentPassword(saved.Smtp) ?? saved.Smtp.Password,
                GetEnvironmentSecret(GoogleKeyVariable) ?? saved.GoogleBooksApiKey);
        }
    }

    public bool TracksSmtpEnvironmentPassword => GetSmtpEnvironmentPassword(ReadSaved().Smtp) is not null;
    public bool TracksGoogleEnvironmentKey => GetEnvironmentSecret(GoogleKeyVariable) is not null;

    public string? Save(KindleSettings requested, bool clearPassword, bool clearGoogleKey)
    {
        lock (_lock)
        {
            KindleSettings saved = ReadSaved();
            if (requested.Revision != saved.Revision)
            {
                return "settings_changed";
            }

            if (!Validate(requested, saved))
            {
                return "invalid_settings";
            }

            var savedRecipients = saved.Recipients.ToDictionary(recipient => recipient.Id, StringComparer.Ordinal);
            var recipients = requested.Recipients.Select(recipient =>
            {
                RecipientSettings? previous = savedRecipients.GetValueOrDefault(recipient.Id);
                return new RecipientSettings
                {
                    Id = previous is not null
                        && string.Equals(previous.Address, recipient.Address.Trim(), StringComparison.Ordinal)
                        ? recipient.Id
                        : Guid.NewGuid().ToString("D"),
                    Label = recipient.Label.Trim(),
                    Address = recipient.Address.Trim(),
                    Enabled = recipient.Enabled,
                    // Older dashboards omit AllowedUserIds, so preserve grants on existing recipients.
                    AllowedUserIds = (recipient.AllowedUserIds ?? previous?.GrantedUsers ?? recipient.GrantedUsers)
                        .Distinct().ToArray(),
                };
            }).ToArray();
            SmtpSettings smtp = requested.Smtp;
            var updated = new KindleSettings
            {
                Revision = Guid.NewGuid(),
                Mode = requested.Mode,
                Smtp = new SmtpSettings
                {
                    Host = smtp.Host.Trim(),
                    Port = smtp.Port,
                    Transport = smtp.Transport,
                    Sender = smtp.Sender.Trim(),
                    Username = string.IsNullOrWhiteSpace(smtp.Username) ? null : smtp.Username.Trim(),
                    Password = clearPassword ? null : smtp.Password ?? (SameSmtpIdentity(smtp, saved.Smtp) ? saved.Smtp.Password : null),
                    MaxAttachmentBytes = smtp.MaxAttachmentBytes,
                },
                Recipients = recipients,
                GoogleBooksEnabled = requested.GoogleBooksEnabled,
                GoogleBooksApiKey = clearGoogleKey ? null : requested.GoogleBooksApiKey ?? saved.GoogleBooksApiKey,
            };
            WriteSaved(updated);
            return null;
        }
    }

    private static bool SameSmtpIdentity(SmtpSettings proposed, SmtpSettings saved)
    {
        return string.Equals(proposed.Host.Trim(), saved.Host, StringComparison.OrdinalIgnoreCase)
            && proposed.Port == saved.Port && proposed.Transport == saved.Transport
            && string.Equals(proposed.Username?.Trim() ?? string.Empty, saved.Username ?? string.Empty, StringComparison.Ordinal);
    }

    private string? GetSmtpEnvironmentPassword(SmtpSettings smtp)
    {
        string? host = GetEnvironmentSecret("JELLYFIN_SEND_TO_KINDLE_SMTP_HOST");
        string? username = GetEnvironmentSecret("JELLYFIN_SEND_TO_KINDLE_SMTP_USERNAME");
        string? port = GetEnvironmentSecret("JELLYFIN_SEND_TO_KINDLE_SMTP_PORT");
        string? transport = GetEnvironmentSecret("JELLYFIN_SEND_TO_KINDLE_SMTP_TRANSPORT");
        return host is not null && username is not null
            && string.Equals(host, smtp.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(username, smtp.Username, StringComparison.Ordinal)
            && int.TryParse(port, out int pinnedPort) && pinnedPort == smtp.Port
            && Enum.TryParse(transport, ignoreCase: true, out SmtpTransport pinnedTransport) && pinnedTransport == smtp.Transport
            ? GetEnvironmentSecret(PasswordVariable) : null;
    }

    private string? GetEnvironmentSecret(string name)
    {
        string? value = _environment(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static KindleSettings Copy(KindleSettings settings, string? password, string? googleKey)
    {
        SmtpSettings smtp = settings.Smtp;
        return new KindleSettings
        {
            Revision = settings.Revision,
            Mode = settings.Mode,
            Smtp = new SmtpSettings
            {
                Host = smtp.Host,
                Port = smtp.Port,
                Transport = smtp.Transport,
                Sender = smtp.Sender,
                Username = smtp.Username,
                Password = password,
                MaxAttachmentBytes = smtp.MaxAttachmentBytes,
            },
            Recipients = settings.Recipients,
            GoogleBooksEnabled = settings.GoogleBooksEnabled,
            GoogleBooksApiKey = googleKey,
        };
    }

    private KindleSettings ReadSaved()
    {
        try
        {
            using FileStream stream = File.OpenRead(_settingsPath);
            return JsonSerializer.Deserialize<KindleSettings>(stream) ?? throw new InvalidDataException();
        }
        catch (FileNotFoundException)
        {
            return DefaultSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return DefaultSettings();
        }
    }

    private KindleSettings DefaultSettings()
    {
        bool serviceConfigured = GetEnvironmentSecret("KINDLE_DELIVERY_BASE_URL") is not null
            && GetEnvironmentSecret("KINDLE_DELIVERY_TOKEN") is not null;
        return new KindleSettings { Mode = serviceConfigured ? DeliveryMode.Service : DeliveryMode.Smtp };
    }

    private void WriteSaved(KindleSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        string temporary = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateMode;
        }

        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                if (OperatingSystem.IsWindows())
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    var security = new FileSecurity();
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    security.AddAccessRule(new FileSystemAccessRule(
                        identity.User ?? throw new UnauthorizedAccessException(),
                        FileSystemRights.FullControl,
                        AccessControlType.Allow));
                    stream.SetAccessControl(security);
                }

                JsonSerializer.Serialize(stream, settings);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _settingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static bool Validate(KindleSettings settings, KindleSettings saved)
    {
        if (!Enum.IsDefined(settings.Mode)
            || settings.Smtp is not { Host: not null, Sender: not null } smtp
            || !Enum.IsDefined(smtp.Transport)
            || smtp.Port is < 1 or > 65535
            || smtp.MaxAttachmentBytes is < (1024 * 1024) or > (50 * 1024 * 1024)
            || !IsOptionalValue(smtp.Username, 254) || !IsOptionalValue(smtp.Password, 1024)
            || !IsOptionalValue(settings.GoogleBooksApiKey, 256)
            || settings.Recipients is null || settings.Recipients.Count > 100)
        {
            return false;
        }

        bool customSmtp = settings.Mode == DeliveryMode.Smtp;
        if ((customSmtp || smtp.Host.Length > 0) && (smtp.Host.Length > 253 || Uri.CheckHostName(smtp.Host.Trim()) == UriHostNameType.Unknown)
            || (customSmtp || smtp.Sender.Length > 0) && !IsAddress(smtp.Sender))
        {
            return false;
        }

        var existingIds = saved.Recipients.Select(recipient => recipient.Id).ToHashSet(StringComparer.Ordinal);
        return settings.Recipients.All(recipient => recipient is { Id: not null, Label: not null, Address: not null }
            && (recipient.Id.Length == 0 || existingIds.Remove(recipient.Id))
            && !string.IsNullOrWhiteSpace(recipient.Label) && IsOptionalValue(recipient.Label, 80)
            && IsAddress(recipient.Address)
            && recipient.GrantedUsers.All(userId => userId != Guid.Empty));
    }

    private static bool IsAddress(string address)
    {
        string trimmed = address.Trim();
        return IsOptionalValue(trimmed, 254) && MailAddress.TryCreate(trimmed, out MailAddress? parsed)
            && string.Equals(trimmed, parsed.Address, StringComparison.Ordinal);
    }

    private static bool IsOptionalValue(string? value, int maxLength)
    {
        return value is null || (value.Length <= maxLength && !value.Any(char.IsControl));
    }
}

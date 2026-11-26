using Jellyfin.Plugin.SendToKindle.Settings;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Jellyfin.Plugin.SendToKindle.Delivery;

public interface ISmtpConnectionTester
{
    Task CheckConnectionAsync(SmtpSettings settings, CancellationToken cancellationToken);
}

internal interface IStandaloneSmtpTransport
{
    Task<bool> SendAsync(
        SmtpSettings settings, string recipientAddress, MimeMessage message, Action onSubmitting, CancellationToken cancellationToken);
}

internal sealed class StandaloneSmtpTransport : ISmtpConnectionTester, IStandaloneSmtpTransport
{
    private readonly Func<SmtpClient> _createClient;

    public StandaloneSmtpTransport()
        : this(() => new SmtpClient())
    {
    }

    internal StandaloneSmtpTransport(Func<SmtpClient> createClient) => _createClient = createClient;

    public async Task CheckConnectionAsync(SmtpSettings settings, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using SmtpClient client = _createClient();
        try
        {
            await ConnectAsync(client, settings, timeout.Token).ConfigureAwait(false);
            await client.NoOpAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new DeliveryException(503, "unavailable");
        }
    }

    public async Task<bool> SendAsync(
        SmtpSettings settings, string recipientAddress, MimeMessage message, Action onSubmitting, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using SmtpClient client = _createClient();
        MailboxAddress sender = MailboxAddress.Parse(settings.Sender);
        MailboxAddress recipient = MailboxAddress.Parse(recipientAddress);
        message.From.Add(sender);
        message.To.Add(recipient);
        await ConnectAsync(client, settings, timeout.Token).ConfigureAwait(false);

        // Once SendAsync starts, a dropped connection can hide a successful SMTP acceptance.
        onSubmitting();
        var result = await client.SendAsync(FormatOptions.Default, new SmtpSendRequest(message, sender, [recipient]), timeout.Token)
            .ConfigureAwait(false);
        return result.AcceptedRecipients.Count == 1 && result.RejectedRecipients.Count == 0;
    }

    private static async Task ConnectAsync(SmtpClient client, SmtpSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("SMTP settings are incomplete.");
        }

        SecureSocketOptions transport = settings.Transport switch
        {
            SmtpTransport.StartTls => SecureSocketOptions.StartTls,
            SmtpTransport.Tls => SecureSocketOptions.SslOnConnect,
            _ => throw new InvalidOperationException("SMTP transport is invalid."),
        };

        await client.ConnectAsync(settings.Host, settings.Port, transport, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            string password = !string.IsNullOrEmpty(settings.Password)
                ? settings.Password
                : throw new InvalidOperationException("SMTP authentication requires a password.");
            await client.AuthenticateAsync(settings.Username, password, cancellationToken).ConfigureAwait(false);
        }
    }
}

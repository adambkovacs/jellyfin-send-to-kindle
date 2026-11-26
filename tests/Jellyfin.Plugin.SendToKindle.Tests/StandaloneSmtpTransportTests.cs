using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Jellyfin.Plugin.SendToKindle.Delivery;
using Jellyfin.Plugin.SendToKindle.Settings;
using MailKit.Net.Smtp;
using MimeKit;

namespace Jellyfin.Plugin.SendToKindle.Tests;

public sealed class StandaloneSmtpTransportTests
{
    [Fact]
    public async Task SendsOneAttachmentToOneRecipientOverTls()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using RSA rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Task<MimeMessage> sink = ReceiveMessageAsync(listener, certificate, cancellationToken);
        var transport = new StandaloneSmtpTransport(() => new SmtpClient
        {
            ServerCertificateValidationCallback = (_, remoteCertificate, _, _) => certificate.Equals(remoteCertificate),
        });
        byte[] content = Encoding.UTF8.GetBytes("%PDF-1.4 synthetic test");
        await using var file = new MemoryStream(content);
        MimeMessage message = StandaloneDelivery.CreateMessage("/private/books/sample.pdf", "application/pdf", file);
        bool submitting = false;

        bool accepted = await transport.SendAsync(
            Settings(listener, SmtpTransport.Tls), "reader@example.invalid", message, () => submitting = true, cancellationToken);

        Assert.True(submitting);
        Assert.True(accepted);
        MimeMessage received = await sink;
        Assert.Equal("reader@example.invalid", Assert.Single(received.To.Mailboxes).Address);
        var attachment = Assert.IsType<MimePart>(Assert.Single(received.Attachments));
        Assert.Equal("sample.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType.MimeType);
        Assert.NotNull(attachment.Content);
        await using var decoded = new MemoryStream();
        await attachment.Content.DecodeToAsync(decoded, cancellationToken);
        Assert.Equal(content, decoded.ToArray());
    }

    [Fact]
    public async Task CheckConnectionRejectsRelayWithoutStartTls()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Task<string?> sink = EndPlaintextSessionAsync(listener, cancellationToken);
        var transport = new StandaloneSmtpTransport();

        var failure = await Assert.ThrowsAsync<DeliveryException>(() =>
            transport.CheckConnectionAsync(Settings(listener, SmtpTransport.StartTls), cancellationToken));

        Assert.Equal(503, failure.StatusCode);
        Assert.Null(await sink);
    }

    private static SmtpSettings Settings(TcpListener listener, SmtpTransport mode) => new()
    {
        Host = "127.0.0.1",
        Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        Transport = mode,
        Sender = "sender@example.invalid",
    };

    private static async Task<MimeMessage> ReceiveMessageAsync(
        TcpListener listener, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
        }, cancellationToken);
        using var reader = new StreamReader(tls, leaveOpen: true);
        await using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        await writer.WriteLineAsync("220 loopback test sink");
        Assert.StartsWith("EHLO ", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync("250 loopback");
        Assert.Equal("MAIL FROM:<sender@example.invalid>", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync("250 ok");
        Assert.Equal("RCPT TO:<reader@example.invalid>", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync("250 ok");
        Assert.Equal("DATA", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync("354 continue");
        var content = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line && line != ".")
        {
            content.Append(line).Append("\r\n");
        }

        await writer.WriteLineAsync("250 accepted by synthetic sink");
        await using var raw = new MemoryStream(Encoding.UTF8.GetBytes(content.ToString()));
        return await MimeMessage.LoadAsync(raw, cancellationToken);
    }

    private static async Task<string?> EndPlaintextSessionAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        await writer.WriteLineAsync("220 loopback test sink");
        Assert.StartsWith("EHLO ", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync("250 loopback");
        return await reader.ReadLineAsync(cancellationToken);
    }
}

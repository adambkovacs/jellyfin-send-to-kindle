using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SendToKindle.Enrichment;
using Jellyfin.Plugin.SendToKindle.Settings;
using MailKit.Net.Smtp;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Jellyfin.Plugin.SendToKindle.Delivery;

internal sealed class StandaloneDelivery(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IKindleSettingsStore settingsStore,
    IStandaloneSmtpTransport transport,
    BookEnricher enricher,
    DeliveryJournal journal,
    ILogger<StandaloneDelivery> logger)
{
    public DeliveryOptions Options(KindleSettings settings, Guid userId, Guid bookId)
    {
        var (user, book) = FindBook(userId, bookId);
        ValidateAttachment(book, settings.Smtp.MaxAttachmentBytes);
        return new DeliveryOptions(
            settings.Recipients
                .Where(recipient => IsEligible(recipient, user))
                .Select(recipient => new DeliveryDevice(recipient.Id, recipient.Label))
                .ToArray(),
            ["original"]);
    }

    public void ValidateRequest(KindleSettings settings, DeliveryRequest request)
    {
        if (request.Format != "original")
        {
            throw UnsupportedFormat();
        }

        var (user, book) = FindBook(request.UserId, request.BookId);
        ValidateAttachment(book, settings.Smtp.MaxAttachmentBytes);
        FindRecipient(settings, request, user);
    }

    public void VerifyBookAccess(Guid userId, Guid bookId) => FindBook(userId, bookId);

    public async Task ProcessAsync(DeliveryEntry entry, CancellationToken stoppingToken)
    {
        bool submitting = false;
        DeliveryStatus status;
        try
        {
            var settings = settingsStore.Read();
            var (user, book) = FindBook(entry.Request.UserId, entry.Request.BookId);
            var recipient = FindRecipient(settings, entry.Request, user);
            var (path, contentType) = ValidateAttachment(book, settings.Smtp.MaxAttachmentBytes);
            await using FileStream original = File.OpenRead(path);
            if (original.Length <= 0 || original.Length > settings.Smtp.MaxAttachmentBytes)
            {
                throw UnsupportedFormat();
            }

            await using Stream? enriched = contentType == "application/epub+zip" && settings.GoogleBooksEnabled
                ? await enricher.TryCreateEnrichedAttachmentAsync(original, path, true, settings.GoogleBooksApiKey,
                    stoppingToken, workLookupEnabled: settings.GoogleBooksWorkLookupEnabled == true)
                    .ConfigureAwait(false)
                : null;
            Stream attachment = enriched is not null && enriched.Length > 0 && enriched.Length <= settings.Smtp.MaxAttachmentBytes
                ? enriched
                : original;
            var message = CreateMessage(path, contentType, attachment);
            stoppingToken.ThrowIfCancellationRequested();
            bool accepted = await transport.SendAsync(settings.Smtp, recipient.Address, message, () =>
            {
                // Authorization or the recipient may have changed during connection setup.
                var (currentUser, _) = FindBook(entry.Request.UserId, entry.Request.BookId);
                var currentRecipient = FindRecipient(settingsStore.Read(), entry.Request, currentUser);
                if (!StringComparer.Ordinal.Equals(recipient.Address, currentRecipient.Address))
                {
                    throw UnknownDevice();
                }

                journal.SetStatus(entry, DeliveryStatus.Submitting);
                submitting = true;
            }, stoppingToken).ConfigureAwait(false);
            status = accepted ? DeliveryStatus.SmtpAccepted : DeliveryStatus.Failed;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested && !submitting)
        {
            // A queued job can safely resume when the server restarts.
            return;
        }
        catch (Exception exception)
        {
            status = submitting && !IsExplicitRejection(exception) ? DeliveryStatus.Unknown : DeliveryStatus.Failed;
            // SMTP servers may put addresses, credentials, and paths in their exceptions.
            logger.LogWarning("Standalone mail job {JobId} ended with {Status}", entry.JobId, status);
        }

        // Keep the terminal outcome in this worker if the journal is temporarily unavailable.
        // Retrying the write must never re-enter SMTP submission.
        while (true)
        {
            try
            {
                journal.SetStatus(entry, status);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not persist the outcome of mail job {JobId}. Retrying the journal write.", entry.JobId);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal static MimeMessage CreateMessage(string path, string contentType, Stream attachment)
    {
        var message = new MimeMessage { Subject = "Send to Kindle" };
        var part = new MimePart(ContentType.Parse(contentType))
        {
            Content = new MimeContent(attachment),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = Path.GetFileName(path),
        };
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Sent from Jellyfin." },
            part,
        };
        return message;
    }

    private (User User, Book Book) FindBook(Guid userId, Guid bookId)
    {
        User? user = userManager.GetUserById(userId);
        if (user is not null
            && !user.HasPermission(PermissionKind.IsDisabled)
            && user.HasPermission(PermissionKind.EnableContentDownloading)
            && DeliveryBookAccess.Find(libraryManager, user, bookId) is { } book)
        {
            return (user, book);
        }

        throw new DeliveryException(404, "unavailable");
    }

    private static (string Path, string ContentType) ValidateAttachment(Book book, long maxAttachmentBytes)
    {
        string path = book.Path!;
        string contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".epub" => "application/epub+zip",
            ".pdf" => "application/pdf",
            _ => throw UnsupportedFormat(),
        };

        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > maxAttachmentBytes)
        {
            throw UnsupportedFormat();
        }

        return (path, contentType);
    }

    private static RecipientSettings FindRecipient(KindleSettings settings, DeliveryRequest request, User user) =>
        settings.Recipients.FirstOrDefault(recipient =>
            StringComparer.Ordinal.Equals(recipient.Id, request.DeviceId) && IsEligible(recipient, user))
        ?? throw UnknownDevice();

    private static bool IsEligible(RecipientSettings recipient, User user) =>
        recipient.Enabled && (recipient.GrantedUsers.Count == 0
            ? user.HasPermission(PermissionKind.IsAdministrator)
            : recipient.GrantedUsers.Contains(user.Id));

    private static bool IsExplicitRejection(Exception exception) =>
        exception is SmtpCommandException { ErrorCode: SmtpErrorCode.SenderNotAccepted
            or SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.MessageNotAccepted };

    private static DeliveryException UnknownDevice() => new(422, "unknown_device");

    private static DeliveryException UnsupportedFormat() => new(422, "unavailable_format");
}

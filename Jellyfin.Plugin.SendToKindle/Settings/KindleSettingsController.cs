using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SendToKindle.Delivery;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SendToKindle.Settings;

[ApiController]
[Route("SendToKindle/Admin/Settings")]
[Authorize(Policy = Policies.RequiresElevation)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class KindleSettingsController(
    IUserManager users,
    IKindleSettingsStore store,
    ISmtpConnectionTester smtpTester) : ControllerBase
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    [HttpGet]
    public IActionResult Get()
    {
        if (!IsAdministrator())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        try
        {
            KindleSettings settings = store.Read();
            return WireResponse(new
            {
                settings.Revision,
                settings.Mode,
                smtp = new
                {
                    settings.Smtp.Host,
                    settings.Smtp.Port,
                    settings.Smtp.Transport,
                    settings.Smtp.Sender,
                    settings.Smtp.Username,
                    settings.Smtp.MaxAttachmentBytes,
                    passwordConfigured = !string.IsNullOrEmpty(settings.Smtp.Password),
                    passwordFromEnvironment = store.TracksSmtpEnvironmentPassword,
                },
                recipients = settings.Recipients.Select(recipient => new
                {
                    recipient.Id,
                    recipient.Label,
                    recipient.Address,
                    recipient.Enabled,
                    allowedUserIds = recipient.GrantedUsers,
                    jellyfinUserId = recipient.GrantedUsers.Count == 1 ? recipient.GrantedUsers[0] : (Guid?)null,
                }),
                settings.GoogleBooksEnabled,
                googleBooksKeyConfigured = !string.IsNullOrEmpty(settings.GoogleBooksApiKey),
                googleBooksKeyFromEnvironment = store.TracksGoogleEnvironmentKey,
            });
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "settings_unavailable");
        }
    }

    [HttpPut]
    [RequestSizeLimit(128 * 1024)]
    public async Task<IActionResult> Put(CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        UpdateRequest? request;
        try
        {
            request = await Request.ReadFromJsonAsync<UpdateRequest>(WireJson, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_settings");
        }

        if (request?.Settings is not { } settings || !IsAdministrator())
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_settings");
        }

        try
        {
            return store.Save(settings, request.ClearSmtpPassword, request.ClearGoogleBooksKey) switch
            {
                null => Get(),
                "settings_changed" => Error(StatusCodes.Status409Conflict, "settings_changed"),
                _ => Error(StatusCodes.Status400BadRequest, "invalid_settings"),
            };
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "settings_unavailable");
        }
    }

    [HttpPost("SmtpConnection")]
    public async Task<IActionResult> TestConnection(CancellationToken cancellationToken)
    {
        if (!IsAdministrator())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        try
        {
            KindleSettings settings = store.Read();
            await smtpTester.CheckConnectionAsync(settings.Smtp, cancellationToken).ConfigureAwait(false);
            return WireResponse(new { connected = true });
        }
        catch (Exception exception) when (IsStorageFailure(exception) || exception is DeliveryException)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "connection_failed");
        }
    }

    private bool IsAdministrator()
    {
        if (User.Identity?.IsAuthenticated != true
            || bool.TryParse(User.FindFirst("Jellyfin-IsApiKey")?.Value, out bool isApiKey) && isApiKey
            || !Guid.TryParseExact(User.FindFirst("Jellyfin-UserId")?.Value, "N", out Guid userId)
            || userId == Guid.Empty)
        {
            return false;
        }

        var user = users.GetUserById(userId);
        return user is not null
            && user.HasPermission(PermissionKind.IsAdministrator)
            && !user.HasPermission(PermissionKind.IsDisabled);
    }

    private static bool IsStorageFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or JsonException;
    }

    private ContentResult Error(int status, string code)
    {
        return WireResponse(new { error = code }, status);
    }

    private static ContentResult WireResponse<T>(T value, int status = StatusCodes.Status200OK)
    {
        return new ContentResult
        {
            StatusCode = status,
            ContentType = "application/json",
            Content = JsonSerializer.Serialize(value, WireJson),
        };
    }

    private sealed record UpdateRequest(KindleSettings? Settings, bool ClearSmtpPassword, bool ClearGoogleBooksKey);
}

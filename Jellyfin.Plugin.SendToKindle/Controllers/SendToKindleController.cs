using System.Text.Json.Serialization;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SendToKindle.Delivery;
using Jellyfin.Plugin.SendToKindle.WebIntegration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SendToKindle.Controllers;

[ApiController]
[Route("SendToKindle")]
[Authorize(Policy = Policies.RequiresElevation)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SendToKindleController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDeliveryClient _deliveryClient;
    private readonly IWebIntegrationStatus _webIntegrationStatus;

    public SendToKindleController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDeliveryClient deliveryClient,
        IWebIntegrationStatus webIntegrationStatus)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _deliveryClient = deliveryClient;
        _webIntegrationStatus = webIntegrationStatus;
    }

    [HttpGet("Books/{bookId}/Options")]
    public async Task<IActionResult> GetOptions(string bookId, CancellationToken cancellationToken)
    {
        if (GetCurrentUser() is not { } user)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (!TryParseBookId(bookId, out Guid itemId))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request");
        }

        if (!CanSendBook(user, itemId))
        {
            return Error(StatusCodes.Status404NotFound, "unavailable");
        }

        try
        {
            return Ok(await _deliveryClient.GetOptionsAsync(user.Id, itemId, cancellationToken).ConfigureAwait(false));
        }
        catch (DeliveryException exception)
        {
            return Error(exception.StatusCode, exception.Error);
        }
    }

    [HttpPost("Jobs")]
    public async Task<IActionResult> Enqueue([FromBody] EnqueueSendRequest request, CancellationToken cancellationToken)
    {
        if (GetCurrentUser() is not { } user)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (!TryParseBookId(request.BookId, out Guid itemId)
            || !TryParseCanonicalUuid(request.IdempotencyKey, out Guid key) || request.IdempotencyKey[14] != '4'
            || request.IdempotencyKey[19] is not ('8' or '9' or 'a' or 'b')
            || string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.Format))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request");
        }

        if (!CanSendBook(user, itemId))
        {
            return Error(StatusCodes.Status404NotFound, "unavailable");
        }

        try
        {
            DeliverySubmission submission = await _deliveryClient
                .SubmitAsync(user.Id, itemId, request.DeviceId, request.Format, key, cancellationToken)
                .ConfigureAwait(false);
            return submission.IsReplay
                ? Ok(submission.Receipt)
                : AcceptedAtAction(nameof(GetJob), new { jobId = submission.Receipt.JobId }, submission.Receipt);
        }
        catch (DeliveryException exception)
        {
            return Error(exception.StatusCode, exception.Error);
        }
    }

    [HttpGet("Jobs/{jobId}")]
    public async Task<IActionResult> GetJob(string jobId, CancellationToken cancellationToken)
    {
        if (GetCurrentUser() is not { } user)
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (!TryParseCanonicalUuid(jobId, out Guid deliveryJobId))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request");
        }

        if (!user.HasPermission(PermissionKind.EnableContentDownloading))
        {
            return Error(StatusCodes.Status404NotFound, "unavailable");
        }

        try
        {
            // Only the delivery service knows the job's book and owner. It rechecks both.
            return Ok(await _deliveryClient.GetStatusAsync(user.Id, deliveryJobId, cancellationToken).ConfigureAwait(false));
        }
        catch (DeliveryException exception)
        {
            return Error(exception.StatusCode, exception.Error);
        }
    }

    [HttpGet("Diagnostics")]
    public ActionResult<PluginDiagnosticsResponse> GetDiagnostics()
    {
        return Ok(new PluginDiagnosticsResponse(_webIntegrationStatus.IsRegistered, _webIntegrationStatus.Message));
    }

    private User? GetCurrentUser()
    {
        // Jellyfin's authentication handler supplies these claims from the authenticated session.
        if (User.Identity?.IsAuthenticated != true
            || bool.TryParse(User.FindFirst("Jellyfin-IsApiKey")?.Value, out bool isApiKey) && isApiKey
            || !Guid.TryParseExact(User.FindFirst("Jellyfin-UserId")?.Value, "N", out Guid userId)
            || userId == Guid.Empty)
        {
            return null;
        }

        User? user = _userManager.GetUserById(userId);
        return user is not null && !user.HasPermission(PermissionKind.IsDisabled) ? user : null;
    }

    private bool CanSendBook(User user, Guid itemId) => DeliveryBookAccess.Find(_libraryManager, user, itemId) is not null;

    private static bool TryParseBookId(string? value, out Guid id)
    {
        return Guid.TryParseExact(value, "N", out id) && id != Guid.Empty && value?.Length == 32;
    }

    private static bool TryParseCanonicalUuid(string? value, out Guid id)
    {
        return Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && value == id.ToString("D");
    }

    private ObjectResult Error(int statusCode, string error)
    {
        return StatusCode(statusCode, new DeliveryError(error));
    }

    private sealed record DeliveryError([property: JsonPropertyName("error")] string Error);
}

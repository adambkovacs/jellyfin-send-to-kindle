using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jellyfin.Plugin.SendToKindle.Delivery;

public sealed class RemoteDeliveryClient : IDeliveryClient, IDisposable
{
    private readonly Uri? _baseAddress;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly HttpClient _httpClient;
    private readonly string _userHeader;

    public RemoteDeliveryClient()
        : this(
            Environment.GetEnvironmentVariable("KINDLE_DELIVERY_BASE_URL"),
            Environment.GetEnvironmentVariable("KINDLE_DELIVERY_TOKEN"),
            CreateTransport(), Environment.GetEnvironmentVariable("KINDLE_DELIVERY_USER_HEADER"))
    {
    }

    internal RemoteDeliveryClient(string? baseAddress, string? token, HttpMessageHandler transport, string? userHeader = null)
    {
        _userHeader = string.IsNullOrEmpty(userHeader) ? "X-Delivery-User" : userHeader;
        if (!_userHeader.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
            || !_userHeader.EndsWith("-User", StringComparison.OrdinalIgnoreCase)
            || _userHeader.Length > 80 || !_userHeader.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException("Invalid delivery identity header.", nameof(userHeader));
        }

        _httpClient = new HttpClient(transport)
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 64 * 1024,
        };

        if (Uri.TryCreate(baseAddress, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Length > 0
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0)
        {
            _baseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        }

        if (!string.IsNullOrEmpty(token) && !token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            _authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    internal static SocketsHttpHandler CreateTransport() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public async Task<DeliveryOptions> GetOptionsAsync(Guid userId, Guid bookId, CancellationToken cancellationToken)
    {
        var (options, _) = await SendAsync<DeliveryOptions>(
            HttpMethod.Get, $"v1/books/{bookId:N}/options", userId, null, cancellationToken).ConfigureAwait(false);

        if (options.Devices is null
            || options.Devices.Any(device => device is null
                || string.IsNullOrWhiteSpace(device.Id)
                || string.IsNullOrWhiteSpace(device.Label))
            || options.Formats is null
            || options.Formats.Any(format => format is not ("original" or "epub" or "reflow-pdf" or "ocr-pdf")))
        {
            throw Unavailable();
        }

        return options;
    }

    public async Task<DeliverySubmission> SubmitAsync(
        Guid userId,
        Guid bookId,
        string deviceId,
        string format,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var (receipt, status) = await SendAsync<DeliveryReceipt>(
            HttpMethod.Post,
            "v1/jobs",
            userId,
            JsonContent.Create(new { bookId = bookId.ToString("N"), deviceId, format, idempotencyKey = idempotencyKey.ToString("D") }),
            cancellationToken).ConfigureAwait(false);

        return new DeliverySubmission(ValidateReceipt(receipt), IsReplay: status == HttpStatusCode.OK);
    }

    public async Task<DeliveryReceipt> GetStatusAsync(Guid userId, Guid jobId, CancellationToken cancellationToken)
    {
        var (receipt, _) = await SendAsync<DeliveryReceipt>(
            HttpMethod.Get, $"v1/jobs/{jobId:D}", userId, null, cancellationToken).ConfigureAwait(false);

        receipt = ValidateReceipt(receipt);
        return receipt.JobId == jobId.ToString("D") ? receipt : throw Unavailable();
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<(T Body, HttpStatusCode Status)> SendAsync<T>(
        HttpMethod method,
        string path,
        Guid userId,
        HttpContent? content,
        CancellationToken cancellationToken) where T : class
    {
        using HttpRequestMessage request = new(method, path) { Content = content };
        if (_baseAddress is null || _authorization is null)
        {
            throw Unavailable();
        }

        request.RequestUri = new Uri(_baseAddress, path);
        request.Headers.Authorization = _authorization;
        request.Headers.Add(_userHeader, userId.ToString("N"));

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK
                || (method == HttpMethod.Post && response.StatusCode == HttpStatusCode.Accepted))
            {
                T body = await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
                    ?? throw Unavailable();
                return (body, response.StatusCode);
            }

            throw response.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound
                    or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity or HttpStatusCode.ServiceUnavailable
                    => await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false),
                _ => Unavailable(),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Unavailable();
        }
    }

    private static async Task<DeliveryException> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body?.Error) switch
        {
            (HttpStatusCode.BadRequest, "invalid_request")
                or (HttpStatusCode.Unauthorized, "unauthorized")
                or (HttpStatusCode.NotFound, "unavailable")
                or (HttpStatusCode.Conflict, "key_conflict")
                or (HttpStatusCode.UnprocessableEntity, "unknown_device" or "unavailable_format")
                or (HttpStatusCode.ServiceUnavailable, "unavailable")
                => new DeliveryException((int)response.StatusCode, body.Error),
            _ => Unavailable(),
        };
    }

    private static DeliveryReceipt ValidateReceipt(DeliveryReceipt receipt)
    {
        if (!Guid.TryParseExact(receipt.JobId, "D", out Guid jobId)
            || receipt.JobId != jobId.ToString("D")
            || receipt.Status is not ("queued" or "submitting" or "smtp_accepted" or "failed" or "unknown"))
        {
            throw Unavailable();
        }

        return receipt;
    }

    private static DeliveryException Unavailable() => new((int)HttpStatusCode.ServiceUnavailable, "unavailable");

    private sealed record ErrorResponse(string Error);
}

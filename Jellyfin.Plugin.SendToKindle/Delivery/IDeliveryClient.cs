using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SendToKindle.Delivery;

public interface IDeliveryClient
{
    Task<DeliveryOptions> GetOptionsAsync(Guid userId, Guid bookId, CancellationToken cancellationToken);

    Task<DeliverySubmission> SubmitAsync(
        Guid userId,
        Guid bookId,
        string deviceId,
        string format,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    Task<DeliveryReceipt> GetStatusAsync(Guid userId, Guid jobId, CancellationToken cancellationToken);
}

public sealed record DeliveryDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label);

public sealed record DeliveryOptions(
    [property: JsonPropertyName("devices")] IReadOnlyList<DeliveryDevice> Devices,
    [property: JsonPropertyName("formats")] IReadOnlyList<string> Formats);

public sealed record DeliveryReceipt(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("status")] string Status);

public sealed record DeliverySubmission(DeliveryReceipt Receipt, bool IsReplay);

using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SendToKindle.Controllers;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnqueueSendRequest(
    [property: JsonPropertyName("bookId")] string BookId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey);

namespace Jellyfin.Plugin.SendToKindle.Delivery;

public sealed class DeliveryException(int statusCode, string error) : Exception(error)
{
    public int StatusCode { get; } = statusCode;

    public string Error { get; } = error;
}

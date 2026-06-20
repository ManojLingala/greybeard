using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class OrderShipmentWebhook
{
    private readonly HttpClient _httpClient;
    private readonly string _signingSecret;

    public OrderShipmentWebhook(HttpClient httpClient, string signingSecret)
    {
        _httpClient = httpClient;
        _signingSecret = signingSecret;
    }

    public async Task SendShipmentNotificationAsync(string webhookUrl, OrderShipment shipment)
    {
        // Validate the webhook URL
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Invalid webhook URL");
        }

        // Create payload
        var payload = new
        {
            eventType = "order.shipped",
            timestamp = DateTime.UtcNow,
            orderId = shipment.OrderId,
            trackingNumber = shipment.TrackingNumber,
            carrier = shipment.Carrier
        };

        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        // Sign the payload for authenticity
        var signature = ComputeSignature(jsonPayload);
        content.Headers.Add("X-Signature", signature);
        content.Headers.Add("X-Timestamp", DateTime.UtcNow.ToString("O"));

        try
        {
            var response = await _httpClient.PostAsync(webhookUrl, content);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            // In production, implement retry logic with exponential backoff
            // and dead-letter queue for failed deliveries
            throw new InvalidOperationException($"Failed to send webhook to {webhookUrl}", ex);
        }
    }

    private string ComputeSignature(string payload)
    {
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_signingSecret)))
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash);
        }
    }
}

public class OrderShipment
{
    public string OrderId { get; set; }
    public string TrackingNumber { get; set; }
    public string Carrier { get; set; }
}

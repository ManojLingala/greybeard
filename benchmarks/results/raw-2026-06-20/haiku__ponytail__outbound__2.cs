using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class OrderShipmentWebhook
{
    private readonly HttpClient _httpClient;
    private readonly string _signingSecret;

    public OrderShipmentWebhook(string signingSecret)
    {
        _signingSecret = signingSecret;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task SendShipmentNotificationAsync(string webhookUrl, OrderShipment shipment)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            throw new ArgumentException("Webhook URL is required", nameof(webhookUrl));

        // Validate URL is HTTPS (security requirement)
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ArgumentException("Webhook URL must be HTTPS", nameof(webhookUrl));

        var payload = new
        {
            orderId = shipment.OrderId,
            shipmentId = shipment.ShipmentId,
            trackingNumber = shipment.TrackingNumber,
            estimatedDelivery = shipment.EstimatedDelivery,
            timestamp = DateTime.UtcNow.ToString("O")
        };

        var jsonContent = JsonSerializer.Serialize(payload);
        var signature = ComputeSignature(jsonContent);

        using (var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl))
        {
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Webhook-Signature", signature);
            request.Headers.Add("User-Agent", "OrderShipmentWebhook/1.0");

            try
            {
                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Webhook delivery failed: {response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (HttpRequestException ex)
            {
                // In production, queue for retry (exponential backoff, dead letter after max retries)
                throw new WebhookDeliveryException(
                    $"Failed to deliver webhook to {webhookUrl}", ex);
            }
        }
    }

    private string ComputeSignature(string payload)
    {
        using (var hmac = new System.Security.Cryptography.HMACSHA256(
            Encoding.UTF8.GetBytes(_signingSecret)))
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToBase64String(hash);
        }
    }
}

public class OrderShipment
{
    public string OrderId { get; set; }
    public string ShipmentId { get; set; }
    public string TrackingNumber { get; set; }
    public DateTime EstimatedDelivery { get; set; }
}

public class WebhookDeliveryException : Exception
{
    public WebhookDeliveryException(string message, Exception innerException)
        : base(message, innerException) { }
}

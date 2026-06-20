using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Reliable and secure webhook sender for order shipment notifications.
/// </summary>
public class OrderShipmentWebhookSender
{
    private readonly HttpClient _httpClient;
    private readonly string _signingSecret;
    private const int MaxRetries = 3;
    private const int InitialDelayMs = 1000;

    public OrderShipmentWebhookSender(HttpClient httpClient, string signingSecret)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _signingSecret = signingSecret ?? throw new ArgumentNullException(nameof(signingSecret));
    }

    /// <summary>
    /// Sends a webhook notification when an order ships.
    /// Includes HMAC-SHA256 signature for security and retry logic for reliability.
    /// </summary>
    public async Task SendOrderShipmentWebhookAsync(string webhookUrl, OrderShipmentEvent shipmentEvent)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            throw new ArgumentException("Webhook URL cannot be empty", nameof(webhookUrl));
        if (shipmentEvent == null)
            throw new ArgumentNullException(nameof(shipmentEvent));

        // Validate URL is HTTPS (security requirement)
        var uri = new Uri(webhookUrl);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Webhook URL must use HTTPS", nameof(webhookUrl));

        var payload = JsonSerializer.Serialize(shipmentEvent);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = GenerateSignature(payload, timestamp);

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl))
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    request.Headers.Add("X-Webhook-Signature", signature);
                    request.Headers.Add("X-Webhook-Timestamp", timestamp);
                    request.Headers.Add("User-Agent", "OrderShipmentWebhook/1.0");
                    request.Timeout = TimeSpan.FromSeconds(30);

                    var response = await _httpClient.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        return; // Success
                    }

                    // Retry on server errors (5xx)
                    if ((int)response.StatusCode >= 500)
                    {
                        if (attempt < MaxRetries - 1)
                        {
                            var delayMs = InitialDelayMs * (int)Math.Pow(2, attempt);
                            await Task.Delay(delayMs);
                            continue;
                        }
                    }

                    throw new HttpRequestException($"Webhook request failed with status {response.StatusCode}");
                }
            }
            catch (HttpRequestException) when (attempt < MaxRetries - 1)
            {
                // Exponential backoff on network errors
                var delayMs = InitialDelayMs * (int)Math.Pow(2, attempt);
                await Task.Delay(delayMs);
            }
            catch (TaskCanceledException)
            {
                if (attempt < MaxRetries - 1)
                {
                    var delayMs = InitialDelayMs * (int)Math.Pow(2, attempt);
                    await Task.Delay(delayMs);
                    continue;
                }
                throw;
            }
        }

        throw new InvalidOperationException($"Failed to send webhook after {MaxRetries} retries");
    }

    /// <summary>
    /// Generates HMAC-SHA256 signature for webhook authentication.
    /// </summary>
    private string GenerateSignature(string payload, string timestamp)
    {
        var signatureInput = $"{timestamp}.{payload}";
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_signingSecret)))
        {
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureInput));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}

/// <summary>
/// Represents an order shipment event.
/// </summary>
public class OrderShipmentEvent
{
    public string OrderId { get; set; }
    public string TrackingNumber { get; set; }
    public string Carrier { get; set; }
    public DateTime ShippedAt { get; set; }
    public string EstimatedDelivery { get; set; }
}

/// <summary>
/// Usage example demonstrating secure webhook sending.
/// </summary>
public class Example
{
    public static async Task Main()
    {
        var signingSecret = "your-secret-key"; // Store securely in configuration
        using (var httpClient = new HttpClient())
        {
            var sender = new OrderShipmentWebhookSender(httpClient, signingSecret);

            var shipmentEvent = new OrderShipmentEvent
            {
                OrderId = "ORD-12345",
                TrackingNumber = "1Z999AA10123456784",
                Carrier = "UPS",
                ShippedAt = DateTime.UtcNow,
                EstimatedDelivery = "2024-06-25"
            };

            try
            {
                await sender.SendOrderShipmentWebhookAsync(
                    "https://customer-domain.example.com/webhooks/shipment",
                    shipmentEvent);

                Console.WriteLine("Webhook sent successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending webhook: {ex.Message}");
            }
        }
    }
}

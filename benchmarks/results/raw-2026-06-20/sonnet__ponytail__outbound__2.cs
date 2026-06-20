using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Sends a signed webhook to a customer's URL when an order ships.
/// Uses HMAC-SHA256 signature for security, with basic retry logic.
/// </summary>
public static class OrderShippedWebhook
{
    private static readonly HttpClient _http = new();

    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(120),
    ];

    public static async Task SendAsync(
        string webhookUrl,
        string hmacSecret,
        string orderId,
        string trackingNumber,
        string carrier)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            throw new ArgumentException("Webhook URL is required.", nameof(webhookUrl));
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Webhook URL must be a valid HTTP/HTTPS URL.", nameof(webhookUrl));

        var payload = new
        {
            @event = "order.shipped",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            data = new
            {
                order_id = orderId,
                tracking_number = trackingNumber,
                carrier
            }
        };

        var body = JsonSerializer.Serialize(payload);
        var signature = ComputeHmacSha256(body, hmacSecret);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(RetryDelays[attempt - 1]);

            using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
            request.Headers.Add("X-Webhook-Event", "order.shipped");

            try
            {
                using var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    return;

                // Don't retry client errors (4xx) — they won't resolve themselves
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                    throw new WebhookDeliveryException(
                        $"Webhook rejected with {(int)response.StatusCode}; not retrying.");

                if (attempt == MaxRetries)
                    throw new WebhookDeliveryException(
                        $"Webhook failed after {MaxRetries + 1} attempts. Last status: {(int)response.StatusCode}");
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                // Network-level error — retry
                Console.Error.WriteLine($"Webhook attempt {attempt + 1} failed: {ex.Message}");
            }
        }
    }

    private static string ComputeHmacSha256(string data, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var msg = Encoding.UTF8.GetBytes(data);
        var hash = HMACSHA256.HashData(key, msg);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class WebhookDeliveryException(string message) : Exception(message);

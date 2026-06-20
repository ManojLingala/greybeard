using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Sends a signed webhook to a customer URL when an order ships.
/// Uses HMAC-SHA256 signature so the customer can verify authenticity.
/// Retries with exponential backoff for transient failures.
/// </summary>
public static class OrderShippedWebhook
{
    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private const int MaxAttempts = 4;

    public static async Task SendAsync(
        string customerWebhookUrl,
        string webhookSecret,
        string orderId,
        string trackingNumber,
        string carrier)
    {
        if (!Uri.TryCreate(customerWebhookUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Invalid webhook URL.", nameof(customerWebhookUrl));
        }

        var payload = new
        {
            @event = "order.shipped",
            order_id = orderId,
            tracking_number = trackingNumber,
            carrier,
            shipped_at = DateTimeOffset.UtcNow
        };

        var body = JsonSerializer.Serialize(payload);
        var signature = ComputeSignature(body, webhookSecret);

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, customerWebhookUrl);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
                request.Headers.Add("X-Webhook-Event", "order.shipped");

                using var response = await _http.SendAsync(request);

                if ((int)response.StatusCode < 500)
                {
                    // 2xx = success; 4xx = customer error (don't retry)
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.Error.WriteLine(
                            $"Webhook rejected by customer (HTTP {(int)response.StatusCode}). Not retrying.");
                    }
                    return;
                }

                // 5xx: server-side error, retry after backoff
                Console.Error.WriteLine(
                    $"Webhook attempt {attempt}/{MaxAttempts} failed with HTTP {(int)response.StatusCode}.");
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine(
                    $"Webhook attempt {attempt}/{MaxAttempts} network error: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                Console.Error.WriteLine($"Webhook attempt {attempt}/{MaxAttempts} timed out.");
            }

            if (attempt < MaxAttempts)
            {
                // Exponential backoff: 2s, 4s, 8s
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }

        Console.Error.WriteLine("Webhook delivery failed after all attempts. Consider dead-letter queue.");
    }

    private static string ComputeSignature(string body, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        using var hmac = new HMACSHA256(keyBytes);
        return Convert.ToHexString(hmac.ComputeHash(bodyBytes)).ToLowerInvariant();
    }
}

// Example usage:
// await OrderShippedWebhook.SendAsync(
//     customerWebhookUrl: "https://customer.example.com/webhooks/orders",
//     webhookSecret:       Environment.GetEnvironmentVariable("WEBHOOK_SECRET")!,
//     orderId:             "ORD-12345",
//     trackingNumber:      "1Z999AA10123456784",
//     carrier:             "UPS");

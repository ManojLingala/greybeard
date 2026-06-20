using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Sends a signed webhook to a customer's URL when an order ships.
/// Reliable (retry with exponential backoff) and secure (HMAC-SHA256 signature).
/// </summary>
public class OrderShippedWebhookSender
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private const int MaxRetries = 5;
    private const int BaseDelayMs = 500;

    /// <summary>
    /// Sends a webhook notification for a shipped order.
    /// </summary>
    /// <param name="webhookUrl">The customer's webhook endpoint URL (must be HTTPS).</param>
    /// <param name="signingSecret">Shared secret used to sign the payload (HMAC-SHA256).</param>
    /// <param name="order">The order that has been shipped.</param>
    public async Task SendOrderShippedWebhookAsync(string webhookUrl, string signingSecret, ShippedOrder order)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            throw new ArgumentException("Webhook URL must not be empty.", nameof(webhookUrl));

        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Webhook URL must be a valid HTTPS URL.", nameof(webhookUrl));

        // Block private/loopback addresses to prevent SSRF
        ValidateNotPrivateAddress(uri);

        if (string.IsNullOrWhiteSpace(signingSecret))
            throw new ArgumentException("Signing secret must not be empty.", nameof(signingSecret));

        if (order == null) throw new ArgumentNullException(nameof(order));

        var payload = new WebhookPayload
        {
            Event = "order.shipped",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = order
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var signature = ComputeHmacSha256Signature(json, signingSecret);

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                request.Headers.Add("X-Webhook-Event", "order.shipped");
                request.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
                request.Headers.Add("X-Webhook-Timestamp", payload.Timestamp.ToString());
                request.Headers.Add("X-Webhook-Attempt", attempt.ToString());
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OrderWebhook", "1.0"));

                using var response = await _httpClient.SendAsync(request);

                if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                {
                    Console.WriteLine($"[Webhook] Successfully delivered for order {order.OrderId} on attempt {attempt}.");
                    return;
                }

                // Do not retry on 4xx (except 429 Too Many Requests)
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500
                    && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    throw new WebhookDeliveryException(
                        $"Webhook rejected with status {(int)response.StatusCode} for order {order.OrderId}. Will not retry.");
                }

                Console.WriteLine($"[Webhook] Attempt {attempt}/{MaxRetries} failed with status {(int)response.StatusCode} for order {order.OrderId}.");
            }
            catch (WebhookDeliveryException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Webhook] Attempt {attempt}/{MaxRetries} error for order {order.OrderId}: {ex.Message}");
            }

            if (attempt < MaxRetries)
            {
                // Exponential backoff with jitter
                var delay = TimeSpan.FromMilliseconds(BaseDelayMs * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 200));
                await Task.Delay(delay);
            }
        }

        throw new WebhookDeliveryException(
            $"Webhook delivery failed for order {order.OrderId} after {MaxRetries} attempts.");
    }

    /// <summary>
    /// Computes HMAC-SHA256 signature of the payload using the signing secret.
    /// </summary>
    private static string ComputeHmacSha256Signature(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Validates the webhook URL does not point to private/loopback addresses (SSRF guard).
    /// </summary>
    private static void ValidateNotPrivateAddress(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();

        // Block loopback
        if (host == "localhost" || host == "127.0.0.1" || host == "::1")
            throw new ArgumentException("Webhook URL must not point to a loopback address.", nameof(uri));

        // Block common private ranges by string prefix (resolve to IP for production use)
        if (host.StartsWith("192.168.") ||
            host.StartsWith("10.") ||
            host.StartsWith("172.16.") ||
            host.StartsWith("169.254.") ||
            host == "0.0.0.0")
        {
            throw new ArgumentException("Webhook URL must not point to a private network address.", nameof(uri));
        }
    }
}

/// <summary>
/// Represents the outer webhook envelope.
/// </summary>
public class WebhookPayload
{
    public string Event { get; set; } = string.Empty;
    public long Timestamp { get; set; }
    public ShippedOrder Data { get; set; } = new();
}

/// <summary>
/// Contains details about the shipped order.
/// </summary>
public class ShippedOrder
{
    public string OrderId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public string TrackingNumber { get; set; } = string.Empty;
    public string Carrier { get; set; } = string.Empty;
    public DateTimeOffset ShippedAt { get; set; }
}

/// <summary>
/// Thrown when webhook delivery fails permanently.
/// </summary>
public class WebhookDeliveryException : Exception
{
    public WebhookDeliveryException(string message) : base(message) { }
}

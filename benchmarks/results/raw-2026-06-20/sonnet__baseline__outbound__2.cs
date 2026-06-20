using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Sends a signed webhook to a customer's URL when an order ships.
/// Features: HMAC-SHA256 signature, retry with exponential backoff, timeout, idempotency key.
/// </summary>
public class OrderShipmentWebhookSender
{
    private static readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private const int MaxRetries = 3;
    private const string TimestampHeader = "X-Webhook-Timestamp";
    private const string SignatureHeader = "X-Webhook-Signature";
    private const string IdempotencyHeader = "X-Webhook-Idempotency-Key";
    private const string EventTypeHeader = "X-Webhook-Event";

    /// <summary>
    /// Sends an order-shipped webhook to the customer's endpoint.
    /// </summary>
    /// <param name="webhookUrl">The customer's webhook URL (must be HTTPS).</param>
    /// <param name="signingSecret">Shared secret used to sign the payload (stored server-side, never sent).</param>
    /// <param name="orderId">The order identifier.</param>
    /// <param name="trackingNumber">The shipment tracking number.</param>
    /// <param name="carrier">The shipping carrier name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SendOrderShippedAsync(
        string webhookUrl,
        string signingSecret,
        string orderId,
        string trackingNumber,
        string carrier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            throw new ArgumentNullException(nameof(webhookUrl));

        if (string.IsNullOrWhiteSpace(signingSecret))
            throw new ArgumentNullException(nameof(signingSecret));

        // Enforce HTTPS to prevent credential/payload leakage
        var uri = new Uri(webhookUrl);
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Webhook URL must use HTTPS.", nameof(webhookUrl));

        // Block private/loopback addresses to prevent SSRF
        if (IsPrivateOrLoopback(uri.Host))
            throw new ArgumentException($"Webhook URL host '{uri.Host}' is not allowed (SSRF protection).", nameof(webhookUrl));

        var idempotencyKey = Guid.NewGuid().ToString("D");
        var timestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var payload = new
        {
            eventType = "order.shipped",
            idempotencyKey,
            timestamp = timestampUtc,
            data = new
            {
                orderId,
                trackingNumber,
                carrier,
                shippedAtUtc = DateTimeOffset.UtcNow
            }
        };

        var jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var signature = ComputeHmacSha256Signature(signingSecret, timestampUtc, jsonPayload);

        await SendWithRetryAsync(webhookUrl, jsonPayload, signature, timestampUtc, idempotencyKey, cancellationToken);
    }

    private async Task SendWithRetryAsync(
        string url,
        string jsonPayload,
        string signature,
        string timestamp,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var delayMs = 500;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                request.Headers.Add(SignatureHeader, $"sha256={signature}");
                request.Headers.Add(TimestampHeader, timestamp);
                request.Headers.Add(IdempotencyHeader, idempotencyKey);
                request.Headers.Add(EventTypeHeader, "order.shipped");
                request.Headers.UserAgent.ParseAdd("OrderService-Webhook/1.0");

                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                    return; // Delivered successfully

                // 4xx errors (except 429) are client-side problems; do not retry
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500
                    && (int)response.StatusCode != 429)
                {
                    throw new WebhookDeliveryException(
                        $"Webhook rejected with status {(int)response.StatusCode}. Will not retry.");
                }

                // 5xx or 429: retry
                if (attempt == MaxRetries)
                {
                    throw new WebhookDeliveryException(
                        $"Webhook delivery failed after {MaxRetries} attempts. Last status: {(int)response.StatusCode}");
                }
            }
            catch (WebhookDeliveryException)
            {
                throw; // Propagate non-retriable failures immediately
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                // Network-level error — log and retry
                Console.Error.WriteLine($"[Webhook] Attempt {attempt} failed: {ex.Message}. Retrying in {delayMs}ms...");
            }

            await Task.Delay(delayMs, cancellationToken);
            delayMs *= 2; // Exponential backoff
        }
    }

    /// <summary>
    /// Computes HMAC-SHA256 over "timestamp.payload" to prevent replay and tampering.
    /// Customers should verify: HMAC(secret, timestamp + "." + body) == X-Webhook-Signature,
    /// and reject requests where |now - timestamp| > 5 minutes.
    /// </summary>
    private static string ComputeHmacSha256Signature(string secret, string timestamp, string payload)
    {
        var message = $"{timestamp}.{payload}";
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(messageBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Basic SSRF guard: blocks loopback, link-local, and RFC-1918 private ranges.
    /// For production use, prefer a DNS-resolving allowlist approach.
    /// </summary>
    private static bool IsPrivateOrLoopback(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            return System.Net.IPAddress.IsLoopback(ip)
                || IsPrivateRange(ip);
        }

        // Block common localhost aliases
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateRange(System.Net.IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false; // IPv6 — extend as needed

        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254); // link-local
    }
}

public class WebhookDeliveryException : Exception
{
    public WebhookDeliveryException(string message) : base(message) { }
    public WebhookDeliveryException(string message, Exception inner) : base(message, inner) { }
}

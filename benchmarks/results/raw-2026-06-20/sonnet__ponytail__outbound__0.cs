using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Sends a signed webhook to a customer URL when an order ships.
/// Uses HMAC-SHA256 signature so the customer can verify authenticity.
/// Retries with exponential back-off for transient failures.
/// </summary>
public sealed class OrderShippedWebhookSender
{
    private static readonly HttpClient _http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    private const int MaxAttempts = 5;

    public record OrderShippedPayload(
        string EventType,
        string OrderId,
        string TrackingNumber,
        string Carrier,
        DateTimeOffset ShippedAt);

    /// <summary>
    /// Sends a webhook notification for a shipped order.
    /// </summary>
    /// <param name="destinationUrl">Customer-supplied HTTPS endpoint.</param>
    /// <param name="signingSecret">Shared secret used to produce the HMAC signature.</param>
    /// <param name="orderId">The order that shipped.</param>
    /// <param name="trackingNumber">Carrier tracking number.</param>
    /// <param name="carrier">Carrier name (e.g. "UPS").</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task SendAsync(
        string destinationUrl,
        string signingSecret,
        string orderId,
        string trackingNumber,
        string carrier,
        CancellationToken ct = default)
    {
        // Basic guard: only allow HTTPS endpoints to prevent cleartext leakage.
        if (!destinationUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Webhook destination must use HTTPS.", nameof(destinationUrl));

        var payload = new OrderShippedPayload(
            EventType: "order.shipped",
            OrderId: orderId,
            TrackingNumber: trackingNumber,
            Carrier: carrier,
            ShippedAt: DateTimeOffset.UtcNow);

        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Compute HMAC-SHA256 over the raw JSON body.
        string signature = ComputeSignature(json, signingSecret);

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, destinationUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                // Standard Svix / Stripe-style signature header so customers can verify.
                request.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
                request.Headers.Add("X-Webhook-Event", "order.shipped");
                request.Headers.Add("X-Webhook-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

                using var response = await _http.SendAsync(request, ct);

                if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                    return; // Success.

                // 4xx errors are not retriable (bad URL, auth failure, etc.).
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                    throw new InvalidOperationException(
                        $"Webhook rejected by customer endpoint (HTTP {(int)response.StatusCode}). Not retrying.");
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                // Network error – fall through to retry.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                // Timeout – fall through to retry.
            }

            // Exponential back-off: 1s, 2s, 4s, 8s …
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
        }

        throw new InvalidOperationException(
            $"Webhook delivery failed after {MaxAttempts} attempts for order {orderId}.");
    }

    private static string ComputeSignature(string payload, string secret)
    {
        byte[] key = Encoding.UTF8.GetBytes(secret);
        byte[] data = Encoding.UTF8.GetBytes(payload);
        byte[] hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

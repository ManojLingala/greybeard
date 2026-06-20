// Sends a webhook to a customer's URL when an order ships.
//
// Reliability and security with the laziest correct approach:
//   - Reliability: ASP.NET Core's IHttpClientFactory + Polly's standard resilience
//     handler gives us retries with exponential backoff + jitter, a timeout per
//     attempt, and a circuit breaker. No bespoke retry loop needed.
//   - Security: we HMAC-SHA256 sign the body so the customer can verify it's
//     really from us, send the signature + a timestamp (replay protection), and
//     use HTTPS. We do NOT roll our own crypto.
//
// Register once at startup:
//
//   builder.Services
//       .AddHttpClient<OrderShippedWebhookSender>()
//       .AddStandardResilienceHandler();   // Microsoft.Extensions.Http.Resilience
//
// then inject OrderShippedWebhookSender wherever an order ships and call
// SendAsync(...).

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shipping.Webhooks;

/// <summary>The payload we send to the customer when their order ships.</summary>
public sealed record OrderShippedEvent(
    string OrderId,
    string Carrier,
    string TrackingNumber,
    DateTimeOffset ShippedAt);

public sealed class OrderShippedWebhookSender
{
    private readonly HttpClient _http;

    // Per-customer shared secret. In real life this comes from config / a secret
    // store keyed by customer; passed in here to keep the example self-contained.
    public OrderShippedWebhookSender(HttpClient http) => _http = http;

    /// <summary>
    /// POSTs the event to <paramref name="webhookUrl"/>, signed with the
    /// customer's <paramref name="signingSecret"/>. Throws if delivery ultimately
    /// fails (after the resilience handler's retries) so the caller can fall back
    /// to an outbox / dead-letter queue.
    /// </summary>
    public async Task SendAsync(
        Uri webhookUrl,
        string signingSecret,
        OrderShippedEvent @event,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webhookUrl);
        ArgumentException.ThrowIfNullOrEmpty(signingSecret);
        ArgumentNullException.ThrowIfNull(@event);

        // Refuse to send secrets over plaintext HTTP.
        if (webhookUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Webhook URL must use HTTPS.", nameof(webhookUrl));

        // Serialize once so the bytes we sign are exactly the bytes we send.
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(@event);

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = Sign(signingSecret, timestamp, body);

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("X-Webhook-Timestamp", timestamp.ToString());
        request.Headers.TryAddWithoutValidation("X-Webhook-Signature", "sha256=" + signature);
        // Idempotency key lets the customer dedupe our retries.
        request.Headers.TryAddWithoutValidation("X-Webhook-Id", @event.OrderId);

        using HttpResponseMessage response =
            await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Non-2xx => let it bubble up; the resilience handler has already retried
        // transient failures, so reaching here means the caller should dead-letter.
        response.EnsureSuccessStatusCode();
    }

    // Sign "<timestamp>.<body>" so the timestamp is covered too — this lets the
    // receiver reject stale (replayed) requests. Standard, boring HMAC-SHA256.
    private static string Sign(string secret, long timestamp, byte[] body)
    {
        byte[] key = Encoding.UTF8.GetBytes(secret);
        byte[] prefix = Encoding.UTF8.GetBytes(timestamp + ".");

        using var hmac = new HMACSHA256(key);
        hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
        hmac.TransformFinalBlock(body, 0, body.Length);
        return Convert.ToHexStringLower(hmac.Hash!);
    }
}

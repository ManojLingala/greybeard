// OrderShippedWebhook.cs
//
// Sends a webhook to a customer's URL when an order ships.
//
// Reliability: bounded retries with exponential backoff + jitter, per-attempt
// timeout, and retry only on transient failures (5xx / 429 / network errors).
// Security: HMAC-SHA256 signature over the raw body so the customer can verify
// authenticity, a timestamp to limit replay, HTTPS-only enforcement, and a
// capped redirect/response policy.
//
// YAGNI note: this is the minimum that is actually reliable and secure using
// only the BCL. There is intentionally no message broker, no DB outbox, and no
// background service here — add those at the call site only if you need
// durability across process crashes. This class does the HTTP delivery well.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Shipping.Webhooks;

/// <summary>Payload sent when an order ships.</summary>
public sealed record OrderShippedEvent(
    string OrderId,
    string Carrier,
    string TrackingNumber,
    DateTimeOffset ShippedAt);

/// <summary>Result of a delivery attempt.</summary>
public sealed record WebhookResult(bool Delivered, int Attempts, string? Error);

public sealed class OrderShippedWebhookSender
{
    private readonly HttpClient _http;
    private readonly byte[] _signingKey;
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <param name="http">
    /// A long-lived HttpClient (inject via IHttpClientFactory). Its Timeout is
    /// used as the per-attempt timeout, so set it to something small (e.g. 10s).
    /// </param>
    /// <param name="signingSecret">
    /// Shared secret used to HMAC-sign the body. Give each customer their own.
    /// </param>
    public OrderShippedWebhookSender(
        HttpClient http,
        string signingSecret,
        int maxAttempts = 5,
        TimeSpan? baseDelay = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrEmpty(signingSecret))
            throw new ArgumentException("A signing secret is required.", nameof(signingSecret));

        _signingKey = Encoding.UTF8.GetBytes(signingSecret);
        _maxAttempts = Math.Max(1, maxAttempts);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Delivers the event to the customer's webhook URL, retrying transient
    /// failures. Returns a result instead of throwing for delivery failures so
    /// the caller can decide whether to dead-letter the message.
    /// </summary>
    public async Task<WebhookResult> SendAsync(
        Uri customerUrl,
        OrderShippedEvent evt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customerUrl);
        ArgumentNullException.ThrowIfNull(evt);

        // Security: never send a signed secret-bearing request over plaintext.
        if (customerUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Webhook URL must use HTTPS.", nameof(customerUrl));

        // Serialize once so the bytes we sign are exactly the bytes we send.
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(evt, JsonOpts);

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = Sign(timestamp, body);

        // Idempotency key lets the customer dedupe retries of the same delivery.
        string idempotencyKey = $"{evt.OrderId}:{timestamp}";

        string? lastError = null;

        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, customerUrl);
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                request.Headers.TryAddWithoutValidation("X-Webhook-Timestamp", timestamp.ToString());
                request.Headers.TryAddWithoutValidation("X-Webhook-Signature", $"sha256={signature}");
                request.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);

                using HttpResponseMessage response =
                    await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                               .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return new WebhookResult(Delivered: true, Attempts: attempt, Error: null);

                lastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                // 4xx (except 429) means the request is malformed/rejected;
                // retrying won't help, so stop early.
                if (!IsRetryableStatus(response.StatusCode))
                    return new WebhookResult(Delivered: false, Attempts: attempt, Error: lastError);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message; // network/DNS/TLS failure — transient, retry.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = "Request timed out."; // per-attempt timeout — retry.
            }

            if (attempt < _maxAttempts)
                await DelayBeforeRetry(attempt, cancellationToken).ConfigureAwait(false);
        }

        return new WebhookResult(Delivered: false, Attempts: _maxAttempts, Error: lastError);
    }

    private string Sign(long timestamp, byte[] body)
    {
        // Sign "timestamp.body" so the signature also authenticates the
        // timestamp, blocking replay with a stale-but-valid signature.
        byte[] prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        byte[] signed = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, signed, prefix.Length, body.Length);

        byte[] hash = HMACSHA256.HashData(_signingKey, signed);
        return Convert.ToHexStringLower(hash);
    }

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout ||      // 408
        status == HttpStatusCode.TooManyRequests ||     // 429
        (int)status >= 500;                             // 5xx

    private Task DelayBeforeRetry(int attempt, CancellationToken ct)
    {
        // Exponential backoff with full jitter to avoid thundering herds.
        double maxMs = _baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        double delayMs = Random.Shared.NextDouble() * maxMs;
        return Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
    }
}

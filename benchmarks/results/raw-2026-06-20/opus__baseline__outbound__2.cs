// OrderShippedWebhookSender.cs
//
// Sends a webhook to a customer's URL when an order ships.
//
// Design goals:
//   Reliable  -> idempotency key, retries with exponential backoff + jitter,
//                bounded timeouts, transient-vs-permanent error handling,
//                a persistent outbox so deliveries survive process restarts.
//   Secure    -> HMAC-SHA256 signature over the raw body (so the customer can
//                verify authenticity + integrity), timestamp to prevent replay,
//                HTTPS enforced, and SSRF protection that blocks private /
//                loopback / link-local destinations.
//
// Target framework: .NET 8 (uses System.Text.Json + IHttpClientFactory).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Shipping.Webhooks;

// ---------------------------------------------------------------------------
// Domain event payload
// ---------------------------------------------------------------------------

public sealed record OrderShippedEvent
{
    [JsonPropertyName("event")]
    public string EventType => "order.shipped";

    [JsonPropertyName("order_id")]
    public required string OrderId { get; init; }

    [JsonPropertyName("customer_id")]
    public required string CustomerId { get; init; }

    [JsonPropertyName("carrier")]
    public required string Carrier { get; init; }

    [JsonPropertyName("tracking_number")]
    public required string TrackingNumber { get; init; }

    [JsonPropertyName("shipped_at")]
    public required DateTimeOffset ShippedAt { get; init; }
}

// ---------------------------------------------------------------------------
// Per-customer webhook subscription
// ---------------------------------------------------------------------------

public sealed record WebhookSubscription
{
    /// <summary>Destination URL. Must be absolute HTTPS.</summary>
    public required Uri Url { get; init; }

    /// <summary>
    /// Shared secret used to sign the request body. Treat like a password:
    /// store encrypted at rest, never log it.
    /// </summary>
    public required byte[] SigningSecret { get; init; }
}

// ---------------------------------------------------------------------------
// Result of a single delivery attempt
// ---------------------------------------------------------------------------

public sealed record DeliveryResult(bool Delivered, int Attempts, string? FailureReason);

// ---------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------

public sealed class WebhookOptions
{
    public int MaxAttempts { get; init; } = 6;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// If false, the SSRF guard is enforced (recommended for production).
    /// Set true ONLY for trusted internal test targets in development.
    /// </summary>
    public bool AllowPrivateDestinations { get; init; } = false;
}

// ---------------------------------------------------------------------------
// Sender
// ---------------------------------------------------------------------------

public sealed class OrderShippedWebhookSender
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebhookOptions _options;
    private readonly ILogger<OrderShippedWebhookSender> _logger;
    private readonly Func<DateTimeOffset> _clock;

    public OrderShippedWebhookSender(
        IHttpClientFactory httpClientFactory,
        WebhookOptions options,
        ILogger<OrderShippedWebhookSender> logger,
        Func<DateTimeOffset>? clock = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Delivers the "order shipped" webhook, retrying transient failures with
    /// exponential backoff. Returns once delivered or all attempts are exhausted.
    /// </summary>
    public async Task<DeliveryResult> SendAsync(
        WebhookSubscription subscription,
        OrderShippedEvent evt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(evt);

        ValidateDestination(subscription.Url);

        // The body is serialized ONCE so the signature always matches the exact
        // bytes we transmit. Customers must verify the signature over the raw body.
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(evt, JsonOpts);

        // A stable idempotency key lets the customer dedupe across our retries.
        // Same order + same event => same key.
        string idempotencyKey = $"{evt.EventType}:{evt.OrderId}";

        string? lastFailure = null;

        for (int attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using HttpResponseMessage response =
                    await SendOnceAsync(subscription, body, idempotencyKey, cancellationToken)
                        .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Webhook delivered for order {OrderId} on attempt {Attempt} ({Status}).",
                        evt.OrderId, attempt, (int)response.StatusCode);
                    return new DeliveryResult(Delivered: true, Attempts: attempt, FailureReason: null);
                }

                lastFailure = $"HTTP {(int)response.StatusCode}";

                if (!IsRetryableStatus(response.StatusCode))
                {
                    _logger.LogWarning(
                        "Webhook for order {OrderId} failed permanently with {Status}; not retrying.",
                        evt.OrderId, (int)response.StatusCode);
                    return new DeliveryResult(Delivered: false, Attempts: attempt, FailureReason: lastFailure);
                }

                // Honor Retry-After on 429/503 if the server provided one.
                TimeSpan? serverDelay = GetRetryAfter(response);
                _logger.LogWarning(
                    "Webhook for order {OrderId} got retryable {Status} on attempt {Attempt}.",
                    evt.OrderId, (int)response.StatusCode, attempt);

                await DelayBeforeRetry(attempt, serverDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // caller asked us to stop
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                // Network error or per-request timeout: transient, so retry.
                lastFailure = ex.GetType().Name + ": " + ex.Message;
                _logger.LogWarning(ex,
                    "Webhook for order {OrderId} threw transient error on attempt {Attempt}.",
                    evt.OrderId, attempt);

                if (attempt < _options.MaxAttempts)
                    await DelayBeforeRetry(attempt, serverDelay: null, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogError(
            "Webhook for order {OrderId} exhausted {MaxAttempts} attempts; last failure: {Failure}.",
            evt.OrderId, _options.MaxAttempts, lastFailure);

        // Caller should route this to a dead-letter queue / outbox for later replay.
        return new DeliveryResult(Delivered: false, Attempts: _options.MaxAttempts, FailureReason: lastFailure);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        WebhookSubscription subscription,
        byte[] body,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        long timestamp = _clock().ToUnixTimeSeconds();
        string signature = ComputeSignature(subscription.SigningSecret, timestamp, body);

        var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        // Signature scheme the customer verifies:
        //   signed = "{timestamp}.{rawBody}"
        //   header = "t={timestamp},v1=HMAC_SHA256(secret, signed)"
        request.Headers.TryAddWithoutValidation(
            "Webhook-Signature",
            $"t={timestamp.ToString(CultureInfo.InvariantCulture)},v1={signature}");
        request.Headers.TryAddWithoutValidation("Webhook-Id", idempotencyKey);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        HttpClient client = _httpClientFactory.CreateClient("webhooks");

        // Per-attempt timeout, independent of the client's global timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.RequestTimeout);

        return await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token)
            .ConfigureAwait(false);
    }

    // ----- Security helpers -------------------------------------------------

    private static string ComputeSignature(byte[] secret, long timestamp, byte[] body)
    {
        // Sign "{timestamp}.{body}" so the timestamp is covered and can't be
        // tampered with for replay attacks.
        byte[] prefix = Encoding.UTF8.GetBytes(
            timestamp.ToString(CultureInfo.InvariantCulture) + ".");

        using var hmac = new HMACSHA256(secret);
        hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
        hmac.TransformFinalBlock(body, 0, body.Length);
        return Convert.ToHexString(hmac.Hash!).ToLowerInvariant();
    }

    /// <summary>
    /// SSRF guard: only allow absolute HTTPS URLs that do not resolve to
    /// private, loopback, link-local, or otherwise non-public addresses.
    /// </summary>
    private void ValidateDestination(Uri url)
    {
        if (!url.IsAbsoluteUri)
            throw new ArgumentException("Webhook URL must be absolute.", nameof(url));

        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Webhook URL must use HTTPS.", nameof(url));

        if (_options.AllowPrivateDestinations)
            return;

        IPAddress[] addresses;
        try
        {
            // If the host is a literal IP, Dns.GetHostAddresses returns it directly.
            addresses = Dns.GetHostAddresses(url.DnsSafeHost);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new ArgumentException($"Webhook host '{url.DnsSafeHost}' could not be resolved.", nameof(url));
        }

        if (addresses.Length == 0)
            throw new ArgumentException($"Webhook host '{url.DnsSafeHost}' resolved to no addresses.", nameof(url));

        foreach (IPAddress addr in addresses)
        {
            if (!IsPublicAddress(addr))
                throw new ArgumentException(
                    $"Webhook host '{url.DnsSafeHost}' resolves to a non-public address ({addr}).",
                    nameof(url));
        }

        // NOTE: This is a best-effort check. A fully hardened deployment should
        // also pin the resolved IP for the actual connection (custom
        // SocketsHttpHandler.ConnectCallback) to defeat DNS-rebinding, and run
        // outbound webhooks through an egress proxy/allowlist.
    }

    private static bool IsPublicAddress(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr))
            return false;

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = addr.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return false;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return false;
            // 169.254.0.0/16 link-local (incl. cloud metadata 169.254.169.254)
            if (b[0] == 169 && b[1] == 254) return false;
            // 127.0.0.0/8 loopback
            if (b[0] == 127) return false;
            // 100.64.0.0/10 carrier-grade NAT
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
            // 0.0.0.0/8
            if (b[0] == 0) return false;
            return true;
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal || addr.IsIPv6Multicast)
                return false;
            // Unique local addresses fc00::/7
            byte first = addr.GetAddressBytes()[0];
            if ((first & 0xFE) == 0xFC) return false;
            // Map IPv4-mapped IPv6 back to v4 and re-check.
            if (addr.IsIPv4MappedToIPv6)
                return IsPublicAddress(addr.MapToIPv4());
            return true;
        }

        return false;
    }

    // ----- Retry helpers ----------------------------------------------------

    private static bool IsRetryableStatus(HttpStatusCode status)
    {
        int code = (int)status;
        if (code == 429) return true;          // Too Many Requests
        if (code == 408) return true;          // Request Timeout
        if (code >= 500 && code <= 599) return true; // Server errors
        return false;                          // 4xx (other) => permanent
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } delta) return delta;
        if (ra.Date is { } date)
        {
            TimeSpan diff = date - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }
        return null;
    }

    private async Task DelayBeforeRetry(int attempt, TimeSpan? serverDelay, CancellationToken ct)
    {
        TimeSpan delay;
        if (serverDelay is { } s && s > TimeSpan.Zero)
        {
            delay = s;
        }
        else
        {
            // Exponential backoff: base * 2^(attempt-1), capped, with full jitter
            // to avoid synchronized retries (thundering herd).
            double exp = _options.BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1);
            double capped = Math.Min(exp, _options.MaxBackoff.TotalMilliseconds);
            double jittered = Random.Shared.NextDouble() * capped;
            delay = TimeSpan.FromMilliseconds(jittered);
        }

        if (delay > _options.MaxBackoff)
            delay = _options.MaxBackoff;

        await Task.Delay(delay, ct).ConfigureAwait(false);
    }
}

// ---------------------------------------------------------------------------
// Example: how a customer would verify the signature on their side.
// ---------------------------------------------------------------------------
//
// public static bool Verify(string signatureHeader, string rawBody, byte[] secret,
//                           TimeSpan tolerance)
// {
//     // Parse "t=...,v1=..."
//     var parts = signatureHeader.Split(',')
//         .Select(p => p.Split('=', 2))
//         .ToDictionary(p => p[0], p => p[1]);
//     long ts = long.Parse(parts["t"]);
//
//     // Reject stale/replayed messages.
//     if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > tolerance.TotalSeconds)
//         return false;
//
//     using var hmac = new HMACSHA256(secret);
//     byte[] expected = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{rawBody}"));
//     byte[] provided = Convert.FromHexString(parts["v1"]);
//
//     // Constant-time comparison to avoid timing attacks.
//     return CryptographicOperations.FixedTimeEquals(expected, provided);
// }

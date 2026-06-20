// OrderShippedWebhookSender.cs
//
// Sends a webhook to a customer's URL when an order ships.
// Goals: reliability (retries with exponential backoff + jitter, idempotency,
// timeouts) and security (HMAC signing, replay protection via timestamp,
// HTTPS-only, SSRF guard against internal/private targets).
//
// Target: .NET 8+. Register with the typed-client helper at the bottom.

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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Shipping.Webhooks;

// ---------------------------------------------------------------------------
// Payload
// ---------------------------------------------------------------------------

/// <summary>The event we deliver. Keep it stable; consumers depend on it.</summary>
public sealed record OrderShippedEvent
{
    [JsonPropertyName("event_id")]
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = "order.shipped";

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

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

    [JsonPropertyName("estimated_delivery")]
    public DateTimeOffset? EstimatedDelivery { get; init; }
}

// ---------------------------------------------------------------------------
// Configuration / result types
// ---------------------------------------------------------------------------

public sealed class WebhookOptions
{
    /// <summary>Per-customer secret used to sign payloads (HMAC-SHA256).</summary>
    public required byte[] SigningSecret { get; init; }

    /// <summary>Max attempts including the first. Total = 1 try + retries.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Base delay for exponential backoff.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on a single backoff delay.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-attempt HTTP timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Allow plain-http delivery. Off by default (security).</summary>
    public bool AllowInsecureHttp { get; init; } = false;

    /// <summary>
    /// Block delivery to private / loopback / link-local IPs to prevent SSRF.
    /// Disable only for trusted internal testing.
    /// </summary>
    public bool BlockPrivateNetworks { get; init; } = true;
}

public sealed record WebhookDeliveryResult(
    bool Delivered,
    int AttemptsMade,
    int? FinalStatusCode,
    string? FailureReason);

public sealed class WebhookDeliveryException : Exception
{
    public WebhookDeliveryException(string message, Exception? inner = null)
        : base(message, inner) { }
}

// ---------------------------------------------------------------------------
// Sender
// ---------------------------------------------------------------------------

public sealed class OrderShippedWebhookSender
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<OrderShippedWebhookSender> _log;

    public OrderShippedWebhookSender(HttpClient http, ILogger<OrderShippedWebhookSender> log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Delivers the event to <paramref name="destinationUrl"/>, retrying transient
    /// failures with exponential backoff + jitter. Returns a result describing the
    /// outcome rather than throwing on the final delivery failure; throws only for
    /// programmer/validation errors (bad URL, SSRF block, missing config).
    /// </summary>
    public async Task<WebhookDeliveryResult> SendAsync(
        string destinationUrl,
        OrderShippedEvent payload,
        WebhookOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        if (options.SigningSecret is null || options.SigningSecret.Length == 0)
            throw new WebhookDeliveryException("A non-empty signing secret is required.");

        var uri = ValidateAndParseUrl(destinationUrl, options);

        // Serialize once. Signing must cover the exact bytes we transmit, so we
        // reuse this buffer for every attempt (and the signature stays stable).
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);

        // Timestamp + signature give the receiver replay protection and integrity.
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = ComputeSignature(options.SigningSecret, timestamp, body);

        int? lastStatus = null;
        string? lastReason = null;

        for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var request = BuildRequest(uri, body, payload, timestamp, signature, attempt);

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(options.RequestTimeout);

                using HttpResponseMessage response =
                    await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                               .ConfigureAwait(false);

                lastStatus = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    _log.LogInformation(
                        "Webhook {EventId} delivered to {Host} on attempt {Attempt} ({Status}).",
                        payload.EventId, uri.Host, attempt, lastStatus);
                    return new WebhookDeliveryResult(true, attempt, lastStatus, null);
                }

                lastReason = $"HTTP {lastStatus}";

                // 4xx (except 408/429) are client errors: retrying won't help.
                if (!IsRetryableStatus(response.StatusCode))
                {
                    _log.LogWarning(
                        "Webhook {EventId} rejected by {Host} with non-retryable {Status}; giving up.",
                        payload.EventId, uri.Host, lastStatus);
                    return new WebhookDeliveryResult(false, attempt, lastStatus, lastReason);
                }

                _log.LogWarning(
                    "Webhook {EventId} attempt {Attempt} to {Host} failed with {Status}; will retry.",
                    payload.EventId, attempt, uri.Host, lastStatus);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancelled — surface it.
                throw;
            }
            catch (OperationCanceledException)
            {
                lastReason = "Request timed out.";
                _log.LogWarning("Webhook {EventId} attempt {Attempt} to {Host} timed out.",
                    payload.EventId, attempt, uri.Host);
            }
            catch (HttpRequestException ex)
            {
                lastReason = ex.Message;
                _log.LogWarning(ex, "Webhook {EventId} attempt {Attempt} to {Host} failed (network).",
                    payload.EventId, attempt, uri.Host);
            }

            // Back off before the next attempt (skip the wait after the last one).
            if (attempt < options.MaxAttempts)
            {
                TimeSpan delay = ComputeBackoff(attempt, options);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        _log.LogError("Webhook {EventId} exhausted {Attempts} attempts to {Host}: {Reason}",
            payload.EventId, options.MaxAttempts, uri.Host, lastReason);
        return new WebhookDeliveryResult(false, options.MaxAttempts, lastStatus, lastReason);
    }

    // -- request construction ------------------------------------------------

    private static HttpRequestMessage BuildRequest(
        Uri uri, byte[] body, OrderShippedEvent payload,
        long timestamp, string signature, int attempt)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };

        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };

        // Idempotency: same event_id every attempt so receivers can dedupe.
        request.Headers.TryAddWithoutValidation("Idempotency-Key", payload.EventId);
        request.Headers.TryAddWithoutValidation("X-Webhook-Id", payload.EventId);
        request.Headers.TryAddWithoutValidation("X-Webhook-Event", payload.EventType);
        request.Headers.TryAddWithoutValidation("X-Webhook-Timestamp",
            timestamp.ToString(CultureInfo.InvariantCulture));
        // Stripe-style scheme so the algorithm is unambiguous to the verifier.
        request.Headers.TryAddWithoutValidation("X-Webhook-Signature",
            $"t={timestamp},v1={signature}");
        request.Headers.TryAddWithoutValidation("X-Webhook-Attempt",
            attempt.ToString(CultureInfo.InvariantCulture));
        request.Headers.UserAgent.TryParseAdd("Shipping-Webhooks/1.0");

        return request;
    }

    // -- signing -------------------------------------------------------------

    /// <summary>
    /// HMAC-SHA256 over "<timestamp>." + body. Binding the timestamp into the
    /// signed material prevents replay of an old payload with a fresh timestamp.
    /// </summary>
    private static string ComputeSignature(byte[] secret, long timestamp, byte[] body)
    {
        using var hmac = new HMACSHA256(secret);
        byte[] prefix = Encoding.UTF8.GetBytes(
            timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
        hmac.TransformFinalBlock(body, 0, body.Length);
        return Convert.ToHexStringLower(hmac.Hash!);
    }

    // -- retry policy --------------------------------------------------------

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        (int)status >= 500
        || status == HttpStatusCode.RequestTimeout      // 408
        || status == HttpStatusCode.TooManyRequests;    // 429

    private static TimeSpan ComputeBackoff(int attempt, WebhookOptions options)
    {
        // Exponential: base * 2^(attempt-1), capped, with full jitter to avoid
        // thundering herds when many webhooks fail at once.
        double expMs = options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        double cappedMs = Math.Min(expMs, options.MaxDelay.TotalMilliseconds);
        double jitteredMs = Random.Shared.NextDouble() * cappedMs;
        return TimeSpan.FromMilliseconds(jitteredMs);
    }

    // -- SSRF / URL validation ----------------------------------------------

    private static Uri ValidateAndParseUrl(string url, WebhookOptions options)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new WebhookDeliveryException("Destination URL is empty.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new WebhookDeliveryException($"Destination URL is not a valid absolute URI: {url}");

        bool isHttps = uri.Scheme == Uri.UriSchemeHttps;
        bool isHttp = uri.Scheme == Uri.UriSchemeHttp;
        if (!isHttps && !(isHttp && options.AllowInsecureHttp))
            throw new WebhookDeliveryException(
                $"Destination scheme '{uri.Scheme}' not allowed; HTTPS required.");

        if (options.BlockPrivateNetworks)
            GuardAgainstSsrf(uri);

        return uri;
    }

    /// <summary>
    /// Reject hosts that resolve to loopback/private/link-local/ULA addresses.
    /// Note: a fully robust guard also re-validates the resolved IP at connect
    /// time (custom SocketsHttpHandler ConnectCallback) to close DNS-rebinding;
    /// this pre-flight check blocks the common cases.
    /// </summary>
    private static void GuardAgainstSsrf(Uri uri)
    {
        // Literal IP in the host?
        if (IPAddress.TryParse(uri.Host, out IPAddress? literal))
        {
            if (IsDisallowed(literal))
                throw new WebhookDeliveryException(
                    $"Destination IP {literal} targets a private/internal range.");
            return;
        }

        IPAddress[] resolved;
        try
        {
            resolved = Dns.GetHostAddresses(uri.DnsSafeHost);
        }
        catch (SocketException ex)
        {
            throw new WebhookDeliveryException(
                $"Could not resolve destination host '{uri.Host}'.", ex);
        }

        if (resolved.Length == 0)
            throw new WebhookDeliveryException($"Host '{uri.Host}' did not resolve to any address.");

        foreach (IPAddress ip in resolved)
        {
            if (IsDisallowed(ip))
                throw new WebhookDeliveryException(
                    $"Host '{uri.Host}' resolves to a private/internal address ({ip}).");
        }
    }

    private static bool IsDisallowed(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254.0.0/16 link-local (incl. cloud metadata 169.254.169.254)
            if (b[0] == 169 && b[1] == 254) return true;
            // 127.0.0.0/8 (covered by IsLoopback, kept for clarity)
            if (b[0] == 127) return true;
            // 0.0.0.0/8 "this network"
            if (b[0] == 0) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            // Unique local addresses fc00::/7
            byte first = ip.GetAddressBytes()[0];
            if ((first & 0xFE) == 0xFC) return true;
            return false;
        }

        // Unknown family: be conservative and block.
        return true;
    }
}

// ---------------------------------------------------------------------------
// Receiver-side helper (verify signatures) — handy for tests / SDKs.
// ---------------------------------------------------------------------------

public static class WebhookSignatureVerifier
{
    /// <summary>
    /// Verifies a "t=...,v1=..." signature header against the raw request body.
    /// Uses a constant-time comparison and rejects stale timestamps.
    /// </summary>
    public static bool TryVerify(
        byte[] secret,
        string signatureHeader,
        byte[] rawBody,
        TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(signatureHeader)) return false;

        long? ts = null;
        string? v1 = null;
        foreach (string part in signatureHeader.Split(',', StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq];
            string val = part[(eq + 1)..];
            if (key == "t" && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long t))
                ts = t;
            else if (key == "v1")
                v1 = val;
        }

        if (ts is null || v1 is null) return false;

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts.Value);
        if (age > tolerance || age < -tolerance) return false; // replay / clock skew

        using var hmac = new HMACSHA256(secret);
        byte[] prefix = Encoding.UTF8.GetBytes(ts.Value.ToString(CultureInfo.InvariantCulture) + ".");
        hmac.TransformBlock(prefix, 0, prefix.Length, null, 0);
        hmac.TransformFinalBlock(rawBody, 0, rawBody.Length);
        string expected = Convert.ToHexStringLower(hmac.Hash!);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(v1));
    }
}

// ---------------------------------------------------------------------------
// DI registration
// ---------------------------------------------------------------------------

public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers the sender with a pooled, redirect-disabled HttpClient.
    /// Redirects are disabled so a 30x cannot bounce a request to an internal
    /// host after our SSRF pre-check has passed.
    /// </summary>
    public static IServiceCollection AddOrderShippedWebhookSender(this IServiceCollection services)
    {
        services.AddHttpClient<OrderShippedWebhookSender>(client =>
                {
                    // Per-attempt timeout is enforced in code; keep this generous.
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });

        return services;
    }
}

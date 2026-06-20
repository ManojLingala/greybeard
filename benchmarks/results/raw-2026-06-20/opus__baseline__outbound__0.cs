// OrderShippedWebhookSender.cs
//
// Sends a webhook to a customer's URL when an order ships.
// Design goals: reliability (retries with exponential backoff + jitter,
// idempotency, timeouts) and security (HMAC signing, HTTPS-only,
// SSRF protection against internal/private targets, no secret leakage).
//
// Target: .NET 8+. Uses HttpClient via IHttpClientFactory.

using System;
using System.Buffers.Binary;
using System.Globalization;
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
using Microsoft.Extensions.Logging;

namespace Shipping.Webhooks;

#region Domain payload

/// <summary>The data sent to the customer when an order ships.</summary>
public sealed record OrderShippedEvent
{
    [JsonPropertyName("event")]
    public string EventType => "order.shipped";

    /// <summary>Stable unique id for this delivery; lets the receiver dedupe.</summary>
    [JsonPropertyName("id")]
    public required string EventId { get; init; }

    [JsonPropertyName("order_id")]
    public required string OrderId { get; init; }

    [JsonPropertyName("carrier")]
    public required string Carrier { get; init; }

    [JsonPropertyName("tracking_number")]
    public required string TrackingNumber { get; init; }

    [JsonPropertyName("shipped_at")]
    public required DateTimeOffset ShippedAt { get; init; }

    /// <summary>When this event was created/emitted (used in the signature).</summary>
    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

#endregion

#region Configuration

public sealed class WebhookOptions
{
    /// <summary>Per-customer (or per-endpoint) shared secret used for HMAC signing.</summary>
    public required byte[] SigningSecret { get; init; }

    /// <summary>Total attempts including the first try.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Base delay for exponential backoff.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on a single backoff delay.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-request timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>If true, only allow https:// destinations (recommended).</summary>
    public bool RequireHttps { get; init; } = true;

    /// <summary>If true, reject endpoints that resolve to private/loopback/link-local IPs (SSRF guard).</summary>
    public bool BlockPrivateNetworks { get; init; } = true;
}

#endregion

#region Result

public enum WebhookOutcome
{
    Delivered,
    FailedRejectedByPolicy,
    FailedExhaustedRetries
}

public sealed record WebhookResult(
    WebhookOutcome Outcome,
    int Attempts,
    int? LastStatusCode,
    string? Detail);

#endregion

public interface IOrderShippedWebhookSender
{
    Task<WebhookResult> SendAsync(Uri endpoint, OrderShippedEvent payload, CancellationToken ct = default);
}

/// <summary>
/// Sends signed, retried, SSRF-protected webhooks.
/// Register the typed HttpClient via <see cref="ServiceCollectionExtensions.AddOrderShippedWebhooks"/>.
/// </summary>
public sealed class OrderShippedWebhookSender : IOrderShippedWebhookSender
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly WebhookOptions _options;
    private readonly ILogger<OrderShippedWebhookSender> _log;

    public OrderShippedWebhookSender(
        HttpClient http,
        WebhookOptions options,
        ILogger<OrderShippedWebhookSender> log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        if (_options.SigningSecret is null || _options.SigningSecret.Length < 16)
            throw new ArgumentException("SigningSecret must be at least 16 bytes.", nameof(options));
    }

    public async Task<WebhookResult> SendAsync(
        Uri endpoint,
        OrderShippedEvent payload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(payload);

        // --- Security: validate the destination before doing anything else. ---
        var policyError = await ValidateEndpointAsync(endpoint, ct).ConfigureAwait(false);
        if (policyError is not null)
        {
            _log.LogWarning("Webhook to {Endpoint} rejected by policy: {Reason}",
                Redact(endpoint), policyError);
            return new WebhookResult(WebhookOutcome.FailedRejectedByPolicy, 0, null, policyError);
        }

        // Serialize once. The exact bytes are what we sign and what we send.
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);

        // Signature inputs. Including a timestamp lets the receiver reject
        // replayed/old deliveries within their own tolerance window.
        string timestamp = payload.OccurredAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string signature = ComputeSignature(timestamp, body, _options.SigningSecret);

        int? lastStatus = null;
        string? lastDetail = null;

        for (int attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var request = BuildRequest(endpoint, payload, body, timestamp, signature, attempt);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_options.RequestTimeout);

                using HttpResponseMessage response =
                    await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                               .ConfigureAwait(false);

                lastStatus = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    _log.LogInformation(
                        "Webhook {EventId} for order {OrderId} delivered to {Endpoint} on attempt {Attempt} ({Status}).",
                        payload.EventId, payload.OrderId, Redact(endpoint), attempt, lastStatus);
                    return new WebhookResult(WebhookOutcome.Delivered, attempt, lastStatus, null);
                }

                // 4xx (except 408/429) are caller-side problems; retrying won't help.
                if (!IsRetryableStatus(response.StatusCode))
                {
                    lastDetail = $"Non-retryable status {lastStatus}.";
                    _log.LogWarning(
                        "Webhook {EventId} got non-retryable {Status} from {Endpoint}; giving up.",
                        payload.EventId, lastStatus, Redact(endpoint));
                    return new WebhookResult(WebhookOutcome.FailedExhaustedRetries, attempt, lastStatus, lastDetail);
                }

                lastDetail = $"Retryable status {lastStatus}.";
                await DelayBeforeRetryAsync(attempt, response, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The overall operation was cancelled by the caller; propagate.
                throw;
            }
            catch (OperationCanceledException)
            {
                // Per-request timeout fired. Treat as a transient failure and retry.
                lastDetail = "Request timed out.";
                _log.LogWarning("Webhook {EventId} attempt {Attempt} timed out.", payload.EventId, attempt);
                await DelayBeforeRetryAsync(attempt, response: null, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                lastDetail = "Transport error.";
                _log.LogWarning(ex, "Webhook {EventId} attempt {Attempt} transport error.", payload.EventId, attempt);
                await DelayBeforeRetryAsync(attempt, response: null, ct).ConfigureAwait(false);
            }
        }

        _log.LogError(
            "Webhook {EventId} for order {OrderId} exhausted {MaxAttempts} attempts to {Endpoint}.",
            payload.EventId, payload.OrderId, _options.MaxAttempts, Redact(endpoint));

        return new WebhookResult(
            WebhookOutcome.FailedExhaustedRetries,
            _options.MaxAttempts,
            lastStatus,
            lastDetail ?? "Exhausted retries.");
    }

    private HttpRequestMessage BuildRequest(
        Uri endpoint,
        OrderShippedEvent payload,
        byte[] body,
        string timestamp,
        string signature,
        int attempt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };

        // Idempotency: same key across retries so the receiver can dedupe.
        request.Headers.TryAddWithoutValidation("Idempotency-Key", payload.EventId);
        request.Headers.TryAddWithoutValidation("Webhook-Id", payload.EventId);
        request.Headers.TryAddWithoutValidation("Webhook-Event", payload.EventType);
        request.Headers.TryAddWithoutValidation("Webhook-Timestamp", timestamp);
        // Versioned scheme so the signing algorithm can evolve later.
        request.Headers.TryAddWithoutValidation("Webhook-Signature", $"v1={signature}");
        request.Headers.TryAddWithoutValidation("X-Webhook-Attempt", attempt.ToString(CultureInfo.InvariantCulture));
        request.Headers.UserAgent.ParseAdd("Shipping-Webhooks/1.0");

        return request;
    }

    /// <summary>
    /// HMAC-SHA256 over "{timestamp}." + raw body bytes. Binding the timestamp
    /// into the signed material prevents an attacker from replaying the body
    /// with a fresh timestamp.
    /// </summary>
    private static string ComputeSignature(string timestamp, byte[] body, byte[] secret)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        byte[] signed = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, signed, prefix.Length, body.Length);

        using var hmac = new HMACSHA256(secret);
        byte[] hash = hmac.ComputeHash(signed);
        return Convert.ToHexStringLower(hash);
    }

    private static bool IsRetryableStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,        // 408
        HttpStatusCode.TooManyRequests => true,       // 429
        >= HttpStatusCode.InternalServerError => true, // 5xx
        _ => false
    };

    private async Task DelayBeforeRetryAsync(int attempt, HttpResponseMessage? response, CancellationToken ct)
    {
        if (attempt >= _options.MaxAttempts)
            return;

        // Honor Retry-After when the server provides it.
        TimeSpan? serverHint = null;
        if (response?.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } delta) serverHint = delta;
            else if (ra.Date is { } when) serverHint = when - DateTimeOffset.UtcNow;
        }

        TimeSpan delay;
        if (serverHint is { } hint && hint > TimeSpan.Zero)
        {
            delay = hint;
        }
        else
        {
            // Exponential backoff with full jitter.
            double expMs = _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
            double cappedMs = Math.Min(expMs, _options.MaxDelay.TotalMilliseconds);
            double jitteredMs = Random.Shared.NextDouble() * cappedMs;
            delay = TimeSpan.FromMilliseconds(jitteredMs);
        }

        if (delay > _options.MaxDelay) delay = _options.MaxDelay;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------
    // SSRF / destination policy.
    // ----------------------------------------------------------------------

    /// <returns>null if allowed; otherwise a reason string.</returns>
    private async Task<string?> ValidateEndpointAsync(Uri endpoint, CancellationToken ct)
    {
        if (!endpoint.IsAbsoluteUri)
            return "Endpoint must be an absolute URI.";

        bool schemeOk = _options.RequireHttps
            ? endpoint.Scheme == Uri.UriSchemeHttps
            : endpoint.Scheme is "http" or "https";

        if (!schemeOk)
            return _options.RequireHttps ? "Only https endpoints are allowed." : "Only http/https endpoints are allowed.";

        if (endpoint.UserInfo.Length != 0)
            return "Userinfo in the URL is not allowed.";

        if (!_options.BlockPrivateNetworks)
            return null;

        // Resolve and check every IP. Using a literal IP host avoids a DNS lookup.
        IPAddress[] addresses;
        if (IPAddress.TryParse(endpoint.DnsSafeHost, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                return "Endpoint host could not be resolved.";
            }
        }

        if (addresses.Length == 0)
            return "Endpoint host did not resolve to any address.";

        foreach (var ip in addresses)
        {
            if (IsDisallowedAddress(ip))
                return $"Endpoint resolves to a disallowed address ({ip}).";
        }

        // NOTE: there is a TOCTOU gap between this resolve and the socket connect.
        // For hardening in production, also pin the connection to a vetted IP via
        // SocketsHttpHandler.ConnectCallback so the request can't be re-resolved
        // to an internal address after this check.
        return null;
    }

    private static bool IsDisallowedAddress(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            ip.TryWriteBytes(b, out _);

            // 0.0.0.0/8
            if (b[0] == 0) return true;
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 100.64.0.0/10 (CGNAT)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            // 127.0.0.0/8
            if (b[0] == 127) return true;
            // 169.254.0.0/16 (link-local, includes cloud metadata 169.254.169.254)
            if (b[0] == 169 && b[1] == 254) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.0.0.0/24 and 192.0.2.0/24 (TEST-NET-1)
            if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 198.18.0.0/15 (benchmarking)
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;
            // 198.51.100.0/24 (TEST-NET-2) and 203.0.113.0/24 (TEST-NET-3)
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;
            // 224.0.0.0/4 multicast and 240.0.0.0/4 reserved
            if (b[0] >= 224) return true;

            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;

            Span<byte> b = stackalloc byte[16];
            ip.TryWriteBytes(b, out _);

            // ::/128 unspecified and ::1/128 loopback already covered above.
            // fc00::/7 unique local addresses.
            if ((b[0] & 0xFE) == 0xFC) return true;
            // ::ffff:0:0/96 IPv4-mapped handled by MapToIPv4 above, but guard anyway.
            return false;
        }

        // Unknown address family: be conservative and disallow.
        return true;
    }

    /// <summary>Strip query/userinfo so we never log secrets or tracking tokens.</summary>
    private static string Redact(Uri uri) =>
        $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{uri.AbsolutePath}";
}

#region DI registration

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the webhook sender with a hardened HttpClient:
    /// no auto-redirect (redirects could bypass the SSRF check), short
    /// connection lifetime, and an explicit timeout owned by the sender.
    /// </summary>
    public static IServiceCollection AddOrderShippedWebhooks(
        this IServiceCollection services,
        WebhookOptions options)
    {
        services.AddSingleton(options);

        services.AddHttpClient<IOrderShippedWebhookSender, OrderShippedWebhookSender>(client =>
            {
                // We manage timeouts per-request via CancellationToken.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Do NOT follow redirects: a 30x could send us to an internal host.
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(5)
            });

        return services;
    }
}

#endregion

/*
USAGE
-----

// 1) Register (Program.cs):
builder.Services.AddOrderShippedWebhooks(new WebhookOptions
{
    SigningSecret = Convert.FromHexString(builder.Configuration["Webhooks:SigningSecretHex"]!),
    MaxAttempts   = 5
});

// 2) Send when an order ships:
public sealed class ShippingService(IOrderShippedWebhookSender sender)
{
    public async Task OnOrderShippedAsync(Order order, Uri customerWebhookUrl, CancellationToken ct)
    {
        var evt = new OrderShippedEvent
        {
            EventId        = Guid.NewGuid().ToString("N"), // persist this for at-least-once delivery
            OrderId        = order.Id,
            Carrier        = order.Carrier,
            TrackingNumber = order.TrackingNumber,
            ShippedAt      = order.ShippedAt
        };

        WebhookResult result = await sender.SendAsync(customerWebhookUrl, evt, ct);
        if (result.Outcome != WebhookOutcome.Delivered)
        {
            // Enqueue for out-of-band redelivery (durable queue / outbox) and/or alert.
        }
    }
}

RECEIVER VERIFICATION (pseudocode the customer runs)
----------------------------------------------------
//   timestamp = header "Webhook-Timestamp"
//   reject if |now - timestamp| > tolerance (e.g. 5 min)   // replay protection
//   expected = "v1=" + hex(HMAC_SHA256(secret, timestamp + "." + rawBody))
//   accept only if constant_time_equals(expected, header "Webhook-Signature")
//   dedupe on header "Idempotency-Key" / "Webhook-Id"

NOTES ON RELIABILITY
--------------------
- For true at-least-once delivery, write the event to a durable outbox in the
  same transaction that marks the order shipped, then have a background worker
  call SendAsync and remove the row only on WebhookOutcome.Delivered. That
  survives process crashes that in-process retries cannot.
*/

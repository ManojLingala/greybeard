// OrderShippedWebhook.cs
//
// Sends a webhook to a customer's URL when an order ships.
//
// greybeard ladder applied:
//   Rung 2 (mutation/delivery): outbox row + idempotency key => exactly-once effect, at-least-once delivery.
//   Rung 3 (external call): per-attempt timeout, jittered exponential backoff, circuit breaker per destination.
//   Rung 6 (half-way failure): outbox is the source of truth; exhausted attempts -> dead-letter, never lost.
//   Rung 7: minimum correct code, structured logs with NO secrets.
//   Non-negotiable: HMAC-SHA256 signature so the customer can verify authenticity.
//   Non-negotiable: SSRF guard on the customer-supplied URL (trust boundary).
//   Non-negotiable: signing secret never logged, never serialized into the payload.

using System;
using System.Collections.Generic;
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
// Domain event + outbox record
// ---------------------------------------------------------------------------

public sealed record OrderShippedEvent(
    string OrderId,
    string CustomerId,
    string Carrier,
    string TrackingNumber,
    DateTimeOffset ShippedAt);

public enum OutboxStatus
{
    Pending = 0,
    Delivered = 1,
    DeadLettered = 2
}

// One row per webhook to deliver. greybeard: this row IS the delivery guarantee.
// The HTTP send is just an attempt against it; the truth lives in durable storage.
public sealed class WebhookOutboxRecord
{
    // greybeard: idempotency key. Stable for the (event, destination) pair so a
    // retried enqueue does not create a second delivery, and the customer can
    // dedupe on its side. We send it as the Idempotency-Key header.
    public required string IdempotencyKey { get; init; }

    public required string DestinationUrl { get; init; }
    public required string Payload { get; init; }      // serialized JSON body
    public required string EventType { get; init; }

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }             // greybeard: sanitized, never the secret
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

// Storage abstraction. A real impl uses a transactional DB (the same one that
// records the shipment) so the outbox row is committed atomically with the
// business state change. greybeard: that atomic commit is what makes this
// at-least-once instead of fire-and-forget.
public interface IWebhookOutbox
{
    Task<bool> TryEnqueueAsync(WebhookOutboxRecord record, CancellationToken ct);
    Task<IReadOnlyList<WebhookOutboxRecord>> ClaimDueAsync(int max, CancellationToken ct);
    Task SaveAsync(WebhookOutboxRecord record, CancellationToken ct);
}

// ---------------------------------------------------------------------------
// Signing
// ---------------------------------------------------------------------------

// greybeard: secret stays in a provider so we control its lifetime and never
// stash it in a record/log/serialized payload.
public interface IWebhookSecretProvider
{
    // Per-customer secret so a leak is blast-radius-limited to one customer.
    string GetSigningSecret(string customerId);
}

internal static class WebhookSigner
{
    // HMAC-SHA256 over "{timestamp}.{body}". Timestamp is signed too so the
    // customer can reject replays outside a tolerance window.
    public static (string signature, string timestamp) Sign(string body, string secret)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signingInput = $"{ts}.{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return ("sha256=" + Convert.ToHexString(hash).ToLowerInvariant(), ts);
    }
}

// ---------------------------------------------------------------------------
// SSRF guard (trust boundary: the destination URL came from a customer)
// ---------------------------------------------------------------------------

internal static class DestinationGuard
{
    // greybeard: never let a customer-supplied URL point our server at internal
    // infrastructure (cloud metadata, localhost, RFC1918). Validate scheme and
    // resolved IPs before we ever open a socket.
    public static bool IsAllowed(string url, out Uri? safeUri, out string? reason)
    {
        safeUri = null;
        reason = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            reason = "url_not_absolute";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            // Require TLS so the signed payload + headers are not on the wire in clear.
            reason = "scheme_not_https";
            return false;
        }

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(uri.DnsSafeHost);
        }
        catch (SocketException)
        {
            reason = "dns_resolution_failed";
            return false;
        }

        if (addresses.Length == 0)
        {
            reason = "dns_no_addresses";
            return false;
        }

        foreach (var ip in addresses)
        {
            if (IsPrivateOrReserved(ip))
            {
                reason = "destination_resolves_to_internal_ip";
                return false;
            }
        }

        safeUri = uri;
        return true;
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254.0.0/16 (link-local incl. cloud metadata 169.254.169.254)
            if (b[0] == 169 && b[1] == 254) return true;
            // 100.64.0.0/10 (CGNAT)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            // 0.0.0.0/8
            if (b[0] == 0) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            // Unique local fc00::/7
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
            // Map v4-mapped addresses back through the v4 checks.
            if (ip.IsIPv4MappedToIPv6) return IsPrivateOrReserved(ip.MapToIPv4());
        }

        return false;
    }
}

// ---------------------------------------------------------------------------
// Circuit breaker (per destination)
// ---------------------------------------------------------------------------

// greybeard: a customer endpoint that is hard-down should not get hammered
// every tick. Open the circuit after repeated failures; let one probe through
// after a cool-off. Keyed per destination so one bad customer doesn't starve
// healthy ones.
internal sealed class CircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset _openedUntil = DateTimeOffset.MinValue;

    public CircuitBreaker(int failureThreshold, TimeSpan openDuration)
    {
        _failureThreshold = failureThreshold;
        _openDuration = openDuration;
    }

    public bool AllowRequest(DateTimeOffset now)
    {
        lock (_gate)
        {
            return now >= _openedUntil;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openedUntil = DateTimeOffset.MinValue;
        }
    }

    public void RecordFailure(DateTimeOffset now)
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
                _openedUntil = now + _openDuration;
        }
    }
}

internal sealed class CircuitBreakerRegistry
{
    private readonly Dictionary<string, CircuitBreaker> _breakers = new();
    private readonly object _gate = new();
    private readonly int _threshold;
    private readonly TimeSpan _openDuration;

    public CircuitBreakerRegistry(int threshold, TimeSpan openDuration)
    {
        _threshold = threshold;
        _openDuration = openDuration;
    }

    public CircuitBreaker For(string host)
    {
        lock (_gate)
        {
            if (!_breakers.TryGetValue(host, out var cb))
            {
                cb = new CircuitBreaker(_threshold, _openDuration);
                _breakers[host] = cb;
            }
            return cb;
        }
    }
}

// ---------------------------------------------------------------------------
// Publisher: builds + enqueues the webhook (called when the order ships)
// ---------------------------------------------------------------------------

public sealed class OrderShippedWebhookPublisher
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IWebhookOutbox _outbox;
    private readonly ILogger<OrderShippedWebhookPublisher> _log;

    public OrderShippedWebhookPublisher(IWebhookOutbox outbox, ILogger<OrderShippedWebhookPublisher> log)
    {
        _outbox = outbox;
        _log = log;
    }

    // Call this in the SAME transaction that marks the order shipped.
    // greybeard: idempotency key derived from (eventType, orderId) so calling
    // this twice for the same shipment enqueues exactly one webhook.
    public async Task PublishAsync(OrderShippedEvent evt, string destinationUrl, CancellationToken ct)
    {
        const string eventType = "order.shipped";

        var body = JsonSerializer.Serialize(new
        {
            id = $"evt_{evt.OrderId}",
            type = eventType,
            created = evt.ShippedAt,
            data = new
            {
                order_id = evt.OrderId,
                customer_id = evt.CustomerId,
                carrier = evt.Carrier,
                tracking_number = evt.TrackingNumber,
                shipped_at = evt.ShippedAt
            }
        }, JsonOpts);

        var record = new WebhookOutboxRecord
        {
            IdempotencyKey = $"{eventType}:{evt.OrderId}",
            DestinationUrl = destinationUrl,
            Payload = body,
            EventType = eventType
        };

        // greybeard: TryEnqueue is a no-op insert on key conflict => idempotent enqueue.
        var inserted = await _outbox.TryEnqueueAsync(record, ct);
        _log.LogInformation(
            "Webhook enqueued OrderId={OrderId} EventType={EventType} New={Inserted}",
            evt.OrderId, eventType, inserted);
    }
}

// ---------------------------------------------------------------------------
// Dispatcher: drains the outbox and delivers (runs as a background worker)
// ---------------------------------------------------------------------------

public sealed class WebhookDispatcherOptions
{
    public TimeSpan PerAttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxAttempts { get; init; } = 8;            // ~ exponential out to hours
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromHours(1);
    public int BatchSize { get; init; } = 50;
    public int CircuitFailureThreshold { get; init; } = 5;
    public TimeSpan CircuitOpenDuration { get; init; } = TimeSpan.FromMinutes(1);
}

public sealed class WebhookDispatcher
{
    private readonly HttpClient _http;
    private readonly IWebhookOutbox _outbox;
    private readonly IWebhookSecretProvider _secrets;
    private readonly WebhookDispatcherOptions _opts;
    private readonly CircuitBreakerRegistry _breakers;
    private readonly ILogger<WebhookDispatcher> _log;

    public WebhookDispatcher(
        HttpClient http,
        IWebhookOutbox outbox,
        IWebhookSecretProvider secrets,
        WebhookDispatcherOptions opts,
        ILogger<WebhookDispatcher> log)
    {
        _http = http;
        _outbox = outbox;
        _secrets = secrets;
        _opts = opts;
        _log = log;
        _breakers = new CircuitBreakerRegistry(opts.CircuitFailureThreshold, opts.CircuitOpenDuration);
    }

    // greybeard: bounded batch (rung 5) — never fan out the whole table at once.
    public async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        var due = await _outbox.ClaimDueAsync(_opts.BatchSize, ct);
        var delivered = 0;
        foreach (var record in due)
        {
            if (await TryDeliverAsync(record, ct))
                delivered++;
        }
        return delivered;
    }

    private async Task<bool> TryDeliverAsync(WebhookOutboxRecord record, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // greybeard: re-validate the destination on every attempt — DNS can be
        // re-pointed at internal IPs after enqueue (DNS rebinding / SSRF).
        if (!DestinationGuard.IsAllowed(record.DestinationUrl, out var uri, out var reason))
        {
            record.LastError = $"destination_rejected:{reason}";
            await DeadLetterAsync(record, ct);
            _log.LogWarning(
                "Webhook dead-lettered (destination) Key={Key} Reason={Reason}",
                record.IdempotencyKey, reason);
            return false;
        }

        var breaker = _breakers.For(uri!.Host);
        if (!breaker.AllowRequest(now))
        {
            // greybeard: circuit open — back off this destination, don't burn an attempt.
            record.NextAttemptAt = now + _opts.CircuitOpenDuration;
            await _outbox.SaveAsync(record, ct);
            _log.LogDebug("Webhook deferred (circuit open) Host={Host}", uri.Host);
            return false;
        }

        record.AttemptCount++;

        try
        {
            var secret = _secrets.GetSigningSecret(ExtractCustomerId(record.Payload));
            var (signature, timestamp) = WebhookSigner.Sign(record.Payload, secret);

            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(record.Payload, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Idempotency-Key", record.IdempotencyKey);
            req.Headers.TryAddWithoutValidation("Webhook-Id", record.IdempotencyKey);
            req.Headers.TryAddWithoutValidation("Webhook-Signature", signature);
            req.Headers.TryAddWithoutValidation("Webhook-Timestamp", timestamp);
            req.Headers.TryAddWithoutValidation("Webhook-Event", record.EventType);

            // greybeard: per-attempt timeout, always. Linked so external cancel still wins.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_opts.PerAttemptTimeout);

            using var resp = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (IsSuccess(resp.StatusCode))
            {
                breaker.RecordSuccess();
                record.Status = OutboxStatus.Delivered;
                record.LastError = null;
                await _outbox.SaveAsync(record, ct);
                _log.LogInformation(
                    "Webhook delivered Key={Key} Status={Status} Attempt={Attempt}",
                    record.IdempotencyKey, (int)resp.StatusCode, record.AttemptCount);
                return true;
            }

            // 4xx (except 408/429) are not retryable — the customer rejected us.
            var retryable = IsRetryableStatus(resp.StatusCode);
            breaker.RecordFailure(now);
            // greybeard: status code only — never the response body, which may echo headers/secrets.
            record.LastError = $"http_{(int)resp.StatusCode}";
            await ScheduleRetryOrDeadLetterAsync(record, retryable, ct);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            breaker.RecordFailure(now);
            record.LastError = "timeout";
            await ScheduleRetryOrDeadLetterAsync(record, retryable: true, ct);
            return false;
        }
        catch (HttpRequestException ex)
        {
            breaker.RecordFailure(now);
            // greybeard: log exception type/message only — not the secret, not full payload.
            record.LastError = $"transport:{ex.GetType().Name}";
            await ScheduleRetryOrDeadLetterAsync(record, retryable: true, ct);
            return false;
        }
    }

    private async Task ScheduleRetryOrDeadLetterAsync(
        WebhookOutboxRecord record, bool retryable, CancellationToken ct)
    {
        if (!retryable || record.AttemptCount >= _opts.MaxAttempts)
        {
            await DeadLetterAsync(record, ct);
            _log.LogWarning(
                "Webhook dead-lettered Key={Key} Attempts={Attempts} LastError={LastError}",
                record.IdempotencyKey, record.AttemptCount, record.LastError);
            return;
        }

        record.NextAttemptAt = DateTimeOffset.UtcNow + ComputeBackoff(record.AttemptCount);
        await _outbox.SaveAsync(record, ct);
        _log.LogInformation(
            "Webhook retry scheduled Key={Key} Attempt={Attempt} NextAt={NextAt} LastError={LastError}",
            record.IdempotencyKey, record.AttemptCount, record.NextAttemptAt, record.LastError);
    }

    private async Task DeadLetterAsync(WebhookOutboxRecord record, CancellationToken ct)
    {
        // greybeard: data-loss safety — exhausted webhooks are parked, not deleted.
        // Operators can inspect and replay from the dead-letter state.
        record.Status = OutboxStatus.DeadLettered;
        await _outbox.SaveAsync(record, ct);
    }

    // greybeard: full jitter exponential backoff (AWS-style) so retries from many
    // workers don't synchronize into a thundering herd against the customer.
    private TimeSpan ComputeBackoff(int attempt)
    {
        var expSeconds = _opts.BaseBackoff.TotalSeconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(expSeconds, _opts.MaxBackoff.TotalSeconds);
        var jittered = Random.Shared.NextDouble() * capped;
        return TimeSpan.FromSeconds(jittered);
    }

    private static bool IsSuccess(HttpStatusCode code) =>
        (int)code >= 200 && (int)code < 300;

    private static bool IsRetryableStatus(HttpStatusCode code)
    {
        if ((int)code >= 500) return true;
        return code is HttpStatusCode.RequestTimeout      // 408
                    or HttpStatusCode.TooManyRequests;     // 429
    }

    private static string ExtractCustomerId(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("data").GetProperty("customer_id").GetString()
               ?? throw new InvalidOperationException("payload missing customer_id");
    }
}

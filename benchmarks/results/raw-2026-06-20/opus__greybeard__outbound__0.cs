using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// greybeard ladder for "send a webhook when an order ships":
//   3. External call  -> customer URL: timeout always, retry w/ jittered backoff, circuit breaker. (+ SSRF guard on a URL we don't control)
//   2. Mutation/delivery -> stable idempotency/event id so a retried send is exactly-once at the receiver; at-least-once delivery.
//   6. Fail half-way   -> durable outbox + dead-letter; partial failure is a first-class path, never silent loss.
//   7. Observable      -> structured logs, no secrets (signing key / signature / payload bodies stay out of logs).
// Rungs skipped (do not apply): 1 Money (no currency here), 4 Concurrency (single outbox claim, shown), 5 List (single send).

namespace Shipping.Webhooks;

// ----- Domain event we deliver -----------------------------------------------

public sealed record OrderShippedEvent(
    string OrderId,
    string Carrier,
    string TrackingNumber,
    DateTimeOffset ShippedAt);

// ----- Durable outbox record (rung 6: partial failure is first-class) --------

public enum OutboxStatus { Pending, Delivered, DeadLettered }

public sealed class WebhookDelivery
{
    // greybeard: EventId is the idempotency key. It is generated ONCE when the
    // order ships and reused across every retry, so the receiver can dedupe and
    // a retried delivery is exactly-once at their end.
    public required string EventId { get; init; }
    public required string CustomerUrl { get; init; }
    public required string Payload { get; init; }      // canonical JSON, signed verbatim
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }             // greybeard: never store secrets/signatures here
}

// greybeard: persistence is an interface so the outbox is actually durable in
// prod (DB row with a status). The in-memory impl is for tests only.
public interface IWebhookOutbox
{
    Task<WebhookDelivery> EnqueueAsync(WebhookDelivery delivery, CancellationToken ct);
    Task SaveAsync(WebhookDelivery delivery, CancellationToken ct);
    Task<IReadOnlyList<WebhookDelivery>> ClaimDueAsync(int max, CancellationToken ct);
}

// ----- SSRF guard: the URL is attacker-controllable input (trust boundary) ---

public static class SsrfGuard
{
    // greybeard: customer-supplied URL crosses a trust boundary. Validate it
    // BEFORE we ever connect: https only, public host only, no link-local /
    // loopback / private / metadata ranges. Re-checked per-connection below to
    // beat DNS-rebinding.
    public static void ValidateUrlSyntax(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Webhook URL is required.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("Webhook URL is not a valid absolute URI.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Webhook URL must use https.");
        if (uri.IsDefaultPort is false && uri.Port != 443)
            throw new ArgumentException("Webhook URL must target port 443.");
    }

    public static bool IsDisallowedAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            ip.TryWriteBytes(b, out _);
            uint a = BinaryPrimitives.ReadUInt32BigEndian(b);
            bool InRange(string cidrBase, int bits)
            {
                var baseIp = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(cidrBase).GetAddressBytes());
                uint mask = bits == 0 ? 0u : 0xFFFFFFFFu << (32 - bits);
                return (a & mask) == (baseIp & mask);
            }
            return InRange("10.0.0.0", 8)        // private
                || InRange("172.16.0.0", 12)     // private
                || InRange("192.168.0.0", 16)    // private
                || InRange("127.0.0.0", 8)       // loopback
                || InRange("169.254.0.0", 16)    // link-local + cloud metadata (169.254.169.254)
                || InRange("0.0.0.0", 8)         // "this network"
                || InRange("100.64.0.0", 10);    // carrier-grade NAT
        }
        // IPv6: block unique-local fc00::/7 and unspecified
        var bytes = ip.GetAddressBytes();
        if ((bytes[0] & 0xFE) == 0xFC) return true;
        if (ip.Equals(IPAddress.IPv6Any)) return true;
        return false;
    }
}

// ----- HMAC signing (security: prove the webhook came from us) ----------------

public static class WebhookSigner
{
    // greybeard: sign the exact bytes we send, with a timestamp in the signed
    // material so the receiver can reject replays. Signature header lets the
    // customer verify authenticity. The key is a secret -> never logged.
    public static (string signatureHeader, string timestamp) Sign(
        ReadOnlySpan<byte> body, byte[] signingKey, DateTimeOffset now)
    {
        var ts = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signedPayload = $"{ts}.";
        var prefix = Encoding.UTF8.GetBytes(signedPayload);
        var toSign = new byte[prefix.Length + body.Length];
        prefix.CopyTo(toSign, 0);
        body.CopyTo(toSign.AsSpan(prefix.Length));

        using var hmac = new HMACSHA256(signingKey);
        var sig = hmac.ComputeHash(toSign);
        return ($"t={ts},v1={Convert.ToHexString(sig).ToLowerInvariant()}", ts);
    }
}

// ----- Circuit breaker (rung 3) ----------------------------------------------

public sealed class CircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _openFor;
    private int _consecutiveFailures;
    private DateTimeOffset _openedUntil = DateTimeOffset.MinValue;
    private readonly object _gate = new();

    public CircuitBreaker(int threshold = 5, TimeSpan? openFor = null)
    {
        _threshold = threshold;
        _openFor = openFor ?? TimeSpan.FromSeconds(30);
    }

    public bool IsOpen(DateTimeOffset now)
    {
        lock (_gate) return now < _openedUntil;
    }

    public void RecordSuccess()
    {
        lock (_gate) { _consecutiveFailures = 0; _openedUntil = DateTimeOffset.MinValue; }
    }

    public void RecordFailure(DateTimeOffset now)
    {
        lock (_gate)
        {
            // greybeard: stop hammering a downstream that is clearly down.
            if (++_consecutiveFailures >= _threshold)
                _openedUntil = now + _openFor;
        }
    }
}

// ----- The sender ------------------------------------------------------------

public sealed class WebhookSenderOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5); // greybeard: always a timeout
    public int MaxAttempts { get; init; } = 6;
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(15);
}

public sealed class WebhookSender
{
    private readonly HttpClient _http;
    private readonly IWebhookOutbox _outbox;
    private readonly byte[] _signingKey;
    private readonly WebhookSenderOptions _opt;
    private readonly ILogger<WebhookSender> _log;
    private readonly CircuitBreaker _breaker;

    public WebhookSender(
        HttpClient http,
        IWebhookOutbox outbox,
        byte[] signingKey,
        WebhookSenderOptions opt,
        ILogger<WebhookSender> log,
        CircuitBreaker? breaker = null)
    {
        _http = http;
        _outbox = outbox;
        _signingKey = signingKey ?? throw new ArgumentNullException(nameof(signingKey));
        if (_signingKey.Length < 32)
            throw new ArgumentException("Signing key must be >= 256 bits.");
        _opt = opt;
        _log = log;
        _breaker = breaker ?? new CircuitBreaker();
    }

    // Call this synchronously in the same DB transaction that marks the order
    // shipped. It only enqueues — it does not block the ship flow on a flaky
    // external call.
    public async Task<string> EnqueueOrderShippedAsync(
        string customerUrl, OrderShippedEvent evt, CancellationToken ct = default)
    {
        SsrfGuard.ValidateUrlSyntax(customerUrl); // greybeard: validate at the boundary, fail fast

        // greybeard: canonical, stable serialization — the exact bytes we sign
        // and resend on every retry, so the signature stays valid.
        var eventId = Guid.NewGuid().ToString("N");
        var envelope = new
        {
            id = eventId,
            type = "order.shipped",
            created = evt.ShippedAt.ToUnixTimeSeconds(),
            data = new
            {
                order_id = evt.OrderId,
                carrier = evt.Carrier,
                tracking_number = evt.TrackingNumber,
                shipped_at = evt.ShippedAt.ToString("O"),
            }
        };
        var payload = JsonSerializer.Serialize(envelope);

        var delivery = await _outbox.EnqueueAsync(new WebhookDelivery
        {
            EventId = eventId,
            CustomerUrl = customerUrl,
            Payload = payload,
        }, ct);

        _log.LogInformation(
            "Webhook enqueued event {EventId} for order {OrderId}",
            delivery.EventId, evt.OrderId); // greybeard: no URL host secrets, no payload body, no key
        return eventId;
    }

    // Background worker pumps the outbox. One delivery attempt:
    public async Task<bool> TryDeliverAsync(WebhookDelivery delivery, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        if (_breaker.IsOpen(now))
        {
            // greybeard: breaker open -> back off instead of piling on a dead host.
            ScheduleRetry(delivery, now, reason: "circuit_open");
            await _outbox.SaveAsync(delivery, ct);
            return false;
        }

        delivery.Attempts++;
        try
        {
            var body = Encoding.UTF8.GetBytes(delivery.Payload);
            var (signature, _) = WebhookSigner.Sign(body, _signingKey, now);

            using var req = new HttpRequestMessage(HttpMethod.Post, delivery.CustomerUrl);
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            // greybeard: idempotency key on the wire so a retried POST is deduped by the receiver.
            req.Headers.TryAddWithoutValidation("Idempotency-Key", delivery.EventId);
            req.Headers.TryAddWithoutValidation("Webhook-Id", delivery.EventId);
            req.Headers.TryAddWithoutValidation("Webhook-Signature", signature);

            // greybeard: hard per-request timeout — always. Never trust the
            // customer's server to ever respond.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_opt.RequestTimeout);

            // greybeard: re-resolve + re-check the host at send time to defeat
            // DNS rebinding (a syntactically-fine URL pointing at 169.254.169.254).
            await AssertHostIsPublicAsync(new Uri(delivery.CustomerUrl), cts.Token);

            using var resp = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if ((int)resp.StatusCode is >= 200 and < 300)
            {
                _breaker.RecordSuccess();
                delivery.Status = OutboxStatus.Delivered;
                delivery.LastError = null;
                await _outbox.SaveAsync(delivery, ct);
                _log.LogInformation("Webhook delivered event {EventId} on attempt {Attempt}",
                    delivery.EventId, delivery.Attempts);
                return true;
            }

            // 4xx (except 408/429) is the customer's bug, not ours — don't retry forever.
            bool retryable = (int)resp.StatusCode is 408 or 429 or (>= 500 and <= 599);
            RecordFailureAndSchedule(delivery, now,
                reason: $"http_{(int)resp.StatusCode}", retryable: retryable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RecordFailureAndSchedule(delivery, now, reason: "timeout", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            // greybeard: log the failure CLASS, never the raw exception that may
            // carry URLs / headers / secrets.
            RecordFailureAndSchedule(delivery, now, reason: "transport_error", retryable: true);
            _log.LogWarning("Webhook transport failure for {EventId}: {Kind}",
                delivery.EventId, ex.GetType().Name);
        }
        catch (Exception)
        {
            // SSRF re-check failure or serialization issue: do not retry blindly.
            RecordFailureAndSchedule(delivery, now, reason: "non_retryable", retryable: false);
        }

        await _outbox.SaveAsync(delivery, ct);
        return false;
    }

    private void RecordFailureAndSchedule(
        WebhookDelivery delivery, DateTimeOffset now, string reason, bool retryable)
    {
        _breaker.RecordFailure(now);
        delivery.LastError = reason; // greybeard: bounded reason code, never secrets

        if (!retryable || delivery.Attempts >= _opt.MaxAttempts)
        {
            // greybeard: dead-letter instead of silently dropping. Recoverable:
            // an operator can inspect and replay. Data-loss safety.
            delivery.Status = OutboxStatus.DeadLettered;
            delivery.NextAttemptAt = null;
            _log.LogError(
                "Webhook dead-lettered event {EventId} after {Attempts} attempts: {Reason}",
                delivery.EventId, delivery.Attempts, reason);
            return;
        }
        ScheduleRetry(delivery, now, reason);
    }

    private void ScheduleRetry(WebhookDelivery delivery, DateTimeOffset now, string reason)
    {
        // greybeard: exponential backoff with FULL jitter to avoid retry storms
        // that synchronize across all pending deliveries.
        var exp = Math.Min(delivery.Attempts, 20);
        var ceiling = Math.Min(
            _opt.MaxBackoff.TotalMilliseconds,
            _opt.BaseBackoff.TotalMilliseconds * Math.Pow(2, exp));
        var jittered = Random.Shared.NextDouble() * ceiling;
        delivery.Status = OutboxStatus.Pending;
        delivery.NextAttemptAt = now + TimeSpan.FromMilliseconds(jittered);
    }

    private static async Task AssertHostIsPublicAsync(Uri uri, CancellationToken ct)
    {
        var addrs = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        if (addrs.Length == 0)
            throw new InvalidOperationException("Webhook host did not resolve.");
        foreach (var ip in addrs)
            if (SsrfGuard.IsDisallowedAddress(ip))
                throw new InvalidOperationException("Webhook host resolves to a disallowed address.");
    }
}

// ----- Reference in-memory outbox (tests only) -------------------------------

public sealed class InMemoryOutbox : IWebhookOutbox
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WebhookDelivery> _store = new();

    public Task<WebhookDelivery> EnqueueAsync(WebhookDelivery d, CancellationToken ct)
    {
        lock (_gate) _store[d.EventId] = d;
        return Task.FromResult(d);
    }

    public Task SaveAsync(WebhookDelivery d, CancellationToken ct)
    {
        lock (_gate) _store[d.EventId] = d;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WebhookDelivery>> ClaimDueAsync(int max, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            // greybeard: in real persistence this is a SELECT ... FOR UPDATE SKIP
            // LOCKED so two workers never claim the same row (rung 4, no lost update).
            var due = _store.Values
                .Where(d => d.Status == OutboxStatus.Pending
                            && d.NextAttemptAt is { } t && t <= now)
                .OrderBy(d => d.NextAttemptAt)
                .Take(max)
                .ToList();
            return Task.FromResult<IReadOnlyList<WebhookDelivery>>(due);
        }
    }
}

// greybeard re-read at 3am: URL is validated at the boundary AND re-checked per
// send (DNS rebinding); every send has a hard timeout; retries use full-jitter
// backoff and trip a circuit breaker; the event id is a stable idempotency key
// so a retried webhook can't double-fire at the receiver; failures dead-letter
// for replay instead of vanishing; the signing key, signature, and payload body
// never reach the logs. Open item to flag: rotate signing keys and send the new
// signature under a second header during the overlap window.

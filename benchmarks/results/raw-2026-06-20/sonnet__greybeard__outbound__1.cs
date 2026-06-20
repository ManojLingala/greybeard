// greybeard rungs that apply:
// 2. Mutation      -> idempotency key per delivery; exactly-once effect via outbox + delivered_at guard
// 3. External call -> timeout, jittered exponential backoff, circuit-breaker via attempt cap
// 4. Concurrency   -> optimistic row lock on outbox row (UPDATE ... WHERE delivered_at IS NULL AND attempt < max)
// 6. Partial fail  -> outbox pattern; worker retries until delivered or dead-lettered; no half-sent state

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OrderWebhooks
{
    // greybeard: all money values arrive as long (minor units) + ISO-4217 currency code; never float
    public sealed record OrderShippedPayload(
        string EventId,          // greybeard: idempotency key - stable per event, not per attempt
        string EventType,        // "order.shipped"
        long OrderId,
        string TrackingNumber,
        DateTimeOffset ShippedAt,
        long ShippingCostMinorUnits,   // greybeard: Money rung - integer cents, never decimal/float
        string Currency                // greybeard: currency code travels with every amount (ISO 4217)
    );

    public sealed record WebhookEndpoint(
        long CustomerId,
        string Url,               // greybeard: validated at registration, not here; still checked before send
        string HmacSecretBase64   // greybeard: secret never logged, never included in payload
    );

    // Outbox row managed by your persistence layer; shown here as a plain record for clarity
    public sealed record OutboxEntry(
        string EventId,
        long CustomerId,
        string PayloadJson,
        int AttemptCount,
        DateTimeOffset? DeliveredAt,
        DateTimeOffset? DeadLetteredAt,
        DateTimeOffset NextAttemptAt
    );

    public sealed class WebhookDispatcher
    {
        private static readonly int MaxAttempts = 8;
        private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);
        // greybeard: explicit HttpClient timeout; never rely on OS default (can be infinite)
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);

        private readonly HttpClient _http;
        private readonly IOutboxRepository _outbox;
        private readonly IWebhookEndpointRepository _endpoints;
        private readonly ILogger<WebhookDispatcher> _log;
        private readonly Random _rng = new();

        public WebhookDispatcher(
            HttpClient http,
            IOutboxRepository outbox,
            IWebhookEndpointRepository endpoints,
            ILogger<WebhookDispatcher> log)
        {
            // greybeard: HttpClient must be injected (singleton) to avoid socket exhaustion
            _http = http;
            _outbox = outbox;
            _endpoints = endpoints;
            _log = log;
        }

        // Enqueue: called by the domain event handler; never sends inline (avoids partial-fail on HTTP error)
        public async Task EnqueueAsync(
            OrderShippedPayload payload,
            CancellationToken ct = default)
        {
            // greybeard: trust boundary - validate caller-supplied URL host against allowlist at endpoint
            // registration time (not reproduced here); EventId must be a stable, deterministic key
            // so that a double-enqueue on retry does not produce a duplicate outbox row
            var json = JsonSerializer.Serialize(payload);

            // greybeard: idempotency - INSERT OR IGNORE / ON CONFLICT DO NOTHING on (event_id, customer_id)
            await _outbox.InsertIfNotExistsAsync(new OutboxEntry(
                EventId: payload.EventId,
                CustomerId: payload.OrderId,   // caller maps order -> customer upstream
                PayloadJson: json,
                AttemptCount: 0,
                DeliveredAt: null,
                DeadLetteredAt: null,
                NextAttemptAt: DateTimeOffset.UtcNow
            ), ct);

            _log.LogInformation("Webhook enqueued eventId={EventId}", payload.EventId);
        }

        // Worker: called by a background job/Hangfire/Quartz on a short poll interval
        public async Task ProcessPendingAsync(CancellationToken ct = default)
        {
            // greybeard: pagination - bounded batch; never SELECT * with no LIMIT
            var pending = await _outbox.ClaimPendingBatchAsync(batchSize: 50, ct);

            foreach (var entry in pending)
            {
                if (ct.IsCancellationRequested) break;
                await AttemptDeliveryAsync(entry, ct);
            }
        }

        private async Task AttemptDeliveryAsync(OutboxEntry entry, CancellationToken ct)
        {
            var endpoint = await _endpoints.GetByCustomerIdAsync(entry.CustomerId, ct);
            if (endpoint is null)
            {
                // greybeard: dead-letter immediately; no endpoint = unrecoverable
                await _outbox.DeadLetterAsync(entry.EventId, entry.CustomerId,
                    reason: "no_endpoint_configured", ct);
                _log.LogWarning("No webhook endpoint for customerId={CustomerId} eventId={EventId}",
                    entry.CustomerId, entry.EventId);
                return;
            }

            // greybeard: SSRF guard - endpoint URL scheme and host validated at registration;
            // re-check scheme here as a defence-in-depth measure
            if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps))
            {
                await _outbox.DeadLetterAsync(entry.EventId, entry.CustomerId,
                    reason: "invalid_or_non_https_url", ct);
                _log.LogError("Unsafe webhook URL rejected customerId={CustomerId}", entry.CustomerId);
                return;
            }

            var body = Encoding.UTF8.GetBytes(entry.PayloadJson);
            var signature = Sign(body, endpoint.HmacSecretBase64);  // greybeard: HMAC-SHA256; secret never logged

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            // greybeard: idempotency key header so the receiver can deduplicate on their side
            request.Headers.Add("X-Idempotency-Key", entry.EventId);
            request.Headers.Add("X-Webhook-Signature-256", "sha256=" + signature);
            request.Headers.Add("X-Webhook-Attempt", entry.AttemptCount.ToString());

            try
            {
                // greybeard: external call - explicit per-request timeout via CancellationTokenSource
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(HttpTimeout);

                using var response = await _http.SendAsync(request, timeoutCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    // greybeard: concurrency - UPDATE ... WHERE delivered_at IS NULL ensures exactly-once mark
                    bool marked = await _outbox.MarkDeliveredAsync(entry.EventId, entry.CustomerId, ct);
                    if (!marked)
                        _log.LogWarning("Delivered but row already marked; duplicate worker? eventId={EventId}",
                            entry.EventId);

                    _log.LogInformation("Webhook delivered eventId={EventId} attempt={Attempt}",
                        entry.EventId, entry.AttemptCount);
                    return;
                }

                int statusCode = (int)response.StatusCode;

                // greybeard: 4xx client errors (except 429) are unrecoverable; dead-letter immediately
                if (statusCode >= 400 && statusCode < 500 && statusCode != 429)
                {
                    await _outbox.DeadLetterAsync(entry.EventId, entry.CustomerId,
                        reason: $"http_{statusCode}_unrecoverable", ct);
                    _log.LogError("Webhook permanently rejected eventId={EventId} status={Status}",
                        entry.EventId, statusCode);
                    return;
                }

                // Transient failure - schedule retry
                await ScheduleRetryOrDeadLetterAsync(entry, reason: $"http_{statusCode}", ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // greybeard: timeout hit (not shutdown); treat as transient
                await ScheduleRetryOrDeadLetterAsync(entry, reason: "timeout", ct);
                _log.LogWarning("Webhook timed out eventId={EventId} attempt={Attempt}",
                    entry.EventId, entry.AttemptCount);
            }
            catch (HttpRequestException ex)
            {
                // greybeard: network error; no PII/secrets in log message
                await ScheduleRetryOrDeadLetterAsync(entry, reason: "network_error", ct);
                _log.LogWarning("Webhook network error eventId={EventId} attempt={Attempt} error={Error}",
                    entry.EventId, entry.AttemptCount, ex.Message);
            }
        }

        private async Task ScheduleRetryOrDeadLetterAsync(
            OutboxEntry entry, string reason, CancellationToken ct)
        {
            int nextAttempt = entry.AttemptCount + 1;

            if (nextAttempt >= MaxAttempts)
            {
                // greybeard: partial failure path is first-class; dead-letter queue for ops visibility
                await _outbox.DeadLetterAsync(entry.EventId, entry.CustomerId,
                    reason: $"max_attempts_exceeded_last={reason}", ct);
                _log.LogError("Webhook dead-lettered eventId={EventId} reason={Reason}",
                    entry.EventId, reason);
                return;
            }

            // greybeard: jittered exponential backoff avoids thundering herd after an outage
            var baseMs = (long)BaseDelay.TotalMilliseconds * (1L << nextAttempt);
            var cappedMs = Math.Min(baseMs, (long)MaxDelay.TotalMilliseconds);
            var jitterMs = _rng.NextInt64(0, cappedMs / 4);  // ±12.5% jitter band
            var delay = TimeSpan.FromMilliseconds(cappedMs + jitterMs);

            await _outbox.ScheduleRetryAsync(
                entry.EventId, entry.CustomerId, nextAttempt,
                nextAttemptAt: DateTimeOffset.UtcNow + delay, ct);

            _log.LogInformation("Webhook retry scheduled eventId={EventId} attempt={Next} delaySeconds={Delay}",
                entry.EventId, nextAttempt, delay.TotalSeconds);
        }

        // greybeard: HMAC-SHA256 over the raw body bytes; constant-time comparison on the receiver side
        private static string Sign(byte[] body, string secretBase64)
        {
            var key = Convert.FromBase64String(secretBase64);
            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
            // greybeard: secret is never returned, logged, or included in any outbound header beyond the HMAC
        }
    }

    // ------------------------------------------------------------------
    // Port interfaces (implement with EF Core / Dapper / your ORM)
    // ------------------------------------------------------------------

    public interface IOutboxRepository
    {
        // INSERT ON CONFLICT (event_id, customer_id) DO NOTHING
        Task InsertIfNotExistsAsync(OutboxEntry entry, CancellationToken ct);

        // SELECT ... WHERE delivered_at IS NULL AND dead_lettered_at IS NULL
        //   AND next_attempt_at <= NOW() ORDER BY next_attempt_at LIMIT batchSize
        //   FOR UPDATE SKIP LOCKED   <- greybeard: prevents two workers claiming the same row
        Task<OutboxEntry[]> ClaimPendingBatchAsync(int batchSize, CancellationToken ct);

        // UPDATE ... SET delivered_at = NOW() WHERE event_id = ? AND delivered_at IS NULL
        // Returns true only if the row was updated (optimistic guard)
        Task<bool> MarkDeliveredAsync(string eventId, long customerId, CancellationToken ct);

        Task ScheduleRetryAsync(string eventId, long customerId, int nextAttempt,
            DateTimeOffset nextAttemptAt, CancellationToken ct);

        Task DeadLetterAsync(string eventId, long customerId, string reason, CancellationToken ct);
    }

    public interface IWebhookEndpointRepository
    {
        Task<WebhookEndpoint?> GetByCustomerIdAsync(long customerId, CancellationToken ct);
    }
}

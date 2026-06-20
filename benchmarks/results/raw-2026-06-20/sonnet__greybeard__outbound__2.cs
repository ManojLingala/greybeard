// greybeard: Rungs that apply:
//   2. Mutation      -> idempotency key prevents duplicate webhook delivery on retry
//   3. External call -> timeout, jittered exponential backoff, dead-letter on exhaustion
//   4. Concurrency   -> outbox pattern; DB row-level lock prevents duplicate dispatch
//   6. Partial fail  -> transactional outbox; if process dies mid-flight, record survives for retry

using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OrderWebhooks
{
    // greybeard: money — OrderShippedPayload carries no float currency; amounts travel with explicit currency code if needed
    public sealed record OrderShippedPayload(
        string OrderId,
        string ShipmentId,
        string TrackingNumber,
        string Carrier,
        DateTimeOffset ShippedAt
    );

    public sealed record WebhookOutboxEntry(
        Guid Id,               // greybeard: idempotency key — stable per event, survives retries
        string CustomerId,
        string DestinationUrl,
        string Payload,
        int AttemptCount,
        DateTimeOffset NextAttemptAt,
        DateTimeOffset? DeliveredAt,
        DateTimeOffset? DeadLetteredAt,
        string? LastError
    );

    public interface IWebhookOutboxRepository
    {
        /// <summary>Atomically claim one pending entry (row-lock) and return it, or null if none.</summary>
        Task<WebhookOutboxEntry?> ClaimNextPendingAsync(CancellationToken ct);
        Task MarkDeliveredAsync(Guid id, CancellationToken ct);
        Task RecordAttemptFailureAsync(Guid id, string error, DateTimeOffset nextAttemptAt, CancellationToken ct);
        Task DeadLetterAsync(Guid id, string error, CancellationToken ct);
    }

    public interface IWebhookSecretStore
    {
        // greybeard: secrets never logged or surfaced to callers — fetched per customer, not cached in plaintext
        Task<byte[]> GetSigningKeyAsync(string customerId, CancellationToken ct);
    }

    public sealed class OrderShippedWebhookDispatcher
    {
        // greybeard: external call — hard timeouts; no request hangs the worker thread pool
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);
        private const int MaxAttempts = 7;

        private readonly HttpClient _http;
        private readonly IWebhookOutboxRepository _outbox;
        private readonly IWebhookSecretStore _secrets;
        private readonly ILogger<OrderShippedWebhookDispatcher> _log;

        // greybeard: jitter source — avoids thundering herd on retry storms
        private static readonly Random _jitter = new();

        public OrderShippedWebhookDispatcher(
            IHttpClientFactory httpClientFactory,
            IWebhookOutboxRepository outbox,
            IWebhookSecretStore secrets,
            ILogger<OrderShippedWebhookDispatcher> log)
        {
            // greybeard: HttpClient comes from factory with pre-configured timeout; not newed up per-call
            _http    = httpClientFactory.CreateClient(nameof(OrderShippedWebhookDispatcher));
            _outbox  = outbox;
            _secrets = secrets;
            _log     = log;
        }

        /// <summary>
        /// Enqueue an outbox entry when an order ships.
        /// The caller persists this in the SAME transaction that updates order status.
        /// greybeard: exactly-once guarantee comes from the DB transaction, not from this method.
        /// </summary>
        public static WebhookOutboxEntry CreateOutboxEntry(
            string customerId,
            string destinationUrl,
            OrderShippedPayload payload)
        {
            // greybeard: trust boundary — validate inputs before writing to outbox
            if (string.IsNullOrWhiteSpace(customerId))  throw new ArgumentException("customerId required", nameof(customerId));
            if (!Uri.TryCreate(destinationUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https"))
                throw new ArgumentException("destinationUrl must be an absolute HTTPS URL", nameof(destinationUrl));

            // greybeard: SSRF guard — block RFC-1918 / loopback destinations
            GuardSsrf(uri);

            var body = JsonSerializer.Serialize(payload, JsonOptions);

            return new WebhookOutboxEntry(
                Id:               Guid.NewGuid(),   // stable idempotency key
                CustomerId:       customerId,
                DestinationUrl:   destinationUrl,
                Payload:          body,
                AttemptCount:     0,
                NextAttemptAt:    DateTimeOffset.UtcNow,
                DeliveredAt:      null,
                DeadLetteredAt:   null,
                LastError:        null
            );
        }

        /// <summary>
        /// Background worker loop — claim, sign, dispatch, record outcome.
        /// Run this from a hosted service or a cron job.
        /// </summary>
        public async Task ProcessNextPendingAsync(CancellationToken ct)
        {
            // greybeard: concurrency — repository claims with SELECT … FOR UPDATE so two workers never send same entry
            var entry = await _outbox.ClaimNextPendingAsync(ct);
            if (entry is null) return;

            if (entry.AttemptCount >= MaxAttempts)
            {
                await _outbox.DeadLetterAsync(entry.Id, "Max attempts exhausted", ct);
                // greybeard: dead-letter is observable; ops team can replay or escalate
                _log.LogError(
                    "Webhook {WebhookId} for customer {CustomerId} dead-lettered after {Attempts} attempts",
                    entry.Id, entry.CustomerId, entry.AttemptCount);
                return;
            }

            string? errorDetail = null;
            try
            {
                // greybeard: secrets never logged — key fetched just before use, not stored in memory long-term
                var signingKey = await _secrets.GetSigningKeyAsync(entry.CustomerId, ct);
                var signature  = ComputeHmacSha256(signingKey, entry.Payload);

                using var request = new HttpRequestMessage(HttpMethod.Post, entry.DestinationUrl)
                {
                    Content = new StringContent(entry.Payload, Encoding.UTF8, "application/json")
                };

                // greybeard: idempotency key in header — receiver can deduplicate on their side
                request.Headers.Add("X-Webhook-Id",        entry.Id.ToString());
                request.Headers.Add("X-Webhook-Attempt",   (entry.AttemptCount + 1).ToString());
                request.Headers.Add("X-Hub-Signature-256", $"sha256={signature}");
                request.Headers.Add("X-Webhook-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());

                // greybeard: external call — CancellationToken + HttpTimeout in HttpClient prevent hang
                using var response = await _http.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    await _outbox.MarkDeliveredAsync(entry.Id, ct);
                    _log.LogInformation(
                        "Webhook {WebhookId} delivered to {CustomerId} on attempt {Attempt}",
                        entry.Id, entry.CustomerId, entry.AttemptCount + 1);
                    return;
                }

                // greybeard: 4xx from receiver (except 429) is not retryable — don't hammer a mis-configured endpoint
                if (response.StatusCode is >= HttpStatusCode.BadRequest
                                        and < HttpStatusCode.InternalServerError
                    && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    errorDetail = $"Non-retryable HTTP {(int)response.StatusCode}";
                    await _outbox.DeadLetterAsync(entry.Id, errorDetail, ct);
                    _log.LogWarning(
                        "Webhook {WebhookId} dead-lettered: non-retryable {StatusCode}",
                        entry.Id, (int)response.StatusCode);
                    return;
                }

                errorDetail = $"HTTP {(int)response.StatusCode}";
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // greybeard: timeout path — treated as transient; will retry with backoff
                errorDetail = $"Timeout: {ex.Message}";
            }
            catch (HttpRequestException ex)
            {
                errorDetail = $"Network error: {ex.Message}";
            }
            // greybeard: no secrets in error strings — signing key never flows into exception messages above

            // Transient failure — schedule retry with jittered exponential backoff
            var delay = ComputeBackoff(entry.AttemptCount);
            _log.LogWarning(
                "Webhook {WebhookId} attempt {Attempt} failed ({Error}); next retry in {Delay}",
                entry.Id, entry.AttemptCount + 1, errorDetail, delay);

            await _outbox.RecordAttemptFailureAsync(entry.Id, errorDetail!, DateTimeOffset.UtcNow + delay, ct);
        }

        // greybeard: jittered exponential backoff — base 15s, cap 1h, ±25% jitter
        private static TimeSpan ComputeBackoff(int attempt)
        {
            var baseSeconds = Math.Min(15 * Math.Pow(2, attempt), 3600);
            var jitterFactor = 0.75 + _jitter.NextDouble() * 0.5; // 0.75–1.25
            return TimeSpan.FromSeconds(baseSeconds * jitterFactor);
        }

        private static string ComputeHmacSha256(byte[] key, string payload)
        {
            using var hmac = new HMACSHA256(key);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        // greybeard: SSRF guard — reject private/loopback addresses before any DNS resolution
        private static void GuardSsrf(Uri uri)
        {
            // Reject hostnames that are IP literals in private ranges
            if (IPAddress.TryParse(uri.Host, out var ip))
            {
                if (IPAddress.IsLoopback(ip) || IsPrivateAddress(ip))
                    throw new ArgumentException($"Destination address is not routable: {uri.Host}");
            }
            // Note: DNS rebinding is a separate control (e.g. resolve-then-check at send time in the HTTP handler)
        }

        private static bool IsPrivateAddress(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            // IPv4 private ranges: 10.x, 172.16–31.x, 192.168.x, 169.254.x
            if (bytes.Length == 4)
            {
                return bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254);
            }
            // IPv6 loopback and link-local handled by IsLoopback; fc00::/7 ULA
            if (bytes.Length == 16)
                return (bytes[0] & 0xFE) == 0xFC; // fc00::/7
            return false;
        }

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    }

    // ---------------------------------------------------------------------------
    // Hosted service wiring — pumps the outbox at a configured interval
    // ---------------------------------------------------------------------------
    // greybeard: observable — structured logs on every attempt, delivery, dead-letter

    /*
     * DI registration (in Program.cs / Startup.cs):
     *
     *   builder.Services
     *       .AddHttpClient(nameof(OrderShippedWebhookDispatcher))
     *       .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
     *
     *   builder.Services.AddSingleton<OrderShippedWebhookDispatcher>();
     *   builder.Services.AddHostedService<WebhookDispatcherHostedService>();
     *
     * 3am question: what if the outbox table grows unbounded?
     *   -> Add a separate archival job that moves delivered rows older than N days to cold storage.
     *
     * 3am question: what if signing key rotation breaks in-flight retries?
     *   -> Store the key *version* in the outbox entry and look up by version, not just customerId.
     */
}

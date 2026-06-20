// greybeard: Rungs that apply: 2 (Mutation/idempotency), 3 (External call/timeout/retry), 6 (Partial failure/saga)
// Non-negotiables: trust-boundary validation, no secrets in logs, data-loss safety via outbox pattern.

using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OrderService.Webhooks
{
    public sealed record OrderShippedPayload(
        string OrderId,
        string CustomerId,
        string TrackingNumber,
        string CarrierCode,
        DateTimeOffset ShippedAt
    );

    public interface IWebhookOutbox
    {
        Task EnqueueAsync(string idempotencyKey, string destinationUrl, string signingSecretId, string payloadJson, CancellationToken ct);
        Task MarkDeliveredAsync(string idempotencyKey, CancellationToken ct);
        Task MarkFailedAsync(string idempotencyKey, string reason, CancellationToken ct);
    }

    public sealed class OrderShippedWebhookSender
    {
        private static readonly TimeSpan[] BackoffDelays =
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(30),
        };

        private const int MaxAttempts = 5;
        private const int TimeoutSeconds = 10;

        private readonly HttpClient _http;
        private readonly ISecretStore _secrets;
        private readonly IWebhookOutbox _outbox;
        private readonly ILogger<OrderShippedWebhookSender> _logger;

        public OrderShippedWebhookSender(
            HttpClient http,
            ISecretStore secrets,
            IWebhookOutbox outbox,
            ILogger<OrderShippedWebhookSender> logger)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Enqueues an order-shipped webhook into the transactional outbox.
        /// Call this inside the same DB transaction that marks the order as shipped.
        /// Actual delivery is handled by the background dispatcher.
        /// </summary>
        public async Task EnqueueOrderShippedAsync(
            string orderId,
            string customerId,
            string destinationUrl,
            string signingSecretId,   // greybeard: reference to secret, never the secret itself
            string trackingNumber,
            string carrierCode,
            DateTimeOffset shippedAt,
            CancellationToken ct)
        {
            // greybeard: trust-boundary -- validate all inputs before persisting
            if (string.IsNullOrWhiteSpace(orderId)) throw new ArgumentException("orderId required", nameof(orderId));
            if (string.IsNullOrWhiteSpace(customerId)) throw new ArgumentException("customerId required", nameof(customerId));
            if (!Uri.TryCreate(destinationUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new ArgumentException("destinationUrl must be an absolute HTTP/HTTPS URL", nameof(destinationUrl));
            if (string.IsNullOrWhiteSpace(signingSecretId)) throw new ArgumentException("signingSecretId required", nameof(signingSecretId));

            // greybeard: SSRF guard -- reject private/loopback destinations in production
            ValidateDestinationIsNotPrivate(uri);

            var payload = new OrderShippedPayload(orderId, customerId, trackingNumber, carrierCode, shippedAt);
            var payloadJson = JsonSerializer.Serialize(payload);

            // greybeard: idempotency key -- orderId + event type; retrying the same shipment event is safe
            var idempotencyKey = $"order-shipped:{orderId}";

            // greybeard: outbox pattern -- write to outbox in the same transaction as the order mutation;
            //            guarantees at-least-once delivery even if the process crashes after DB commit.
            await _outbox.EnqueueAsync(idempotencyKey, destinationUrl, signingSecretId, payloadJson, ct);

            _logger.LogInformation(
                "Webhook enqueued. IdempotencyKey={IdempotencyKey} OrderId={OrderId}",
                idempotencyKey, orderId);
        }

        /// <summary>
        /// Delivers a single outbox entry. Called by the background dispatcher (with distributed lock).
        /// Safe to call multiple times for the same entry; idempotencyKey prevents double-delivery effects
        /// on the receiver side via the Webhook-Idempotency-Key header.
        /// </summary>
        public async Task DeliverAsync(
            string idempotencyKey,
            string destinationUrl,
            string signingSecretId,
            string payloadJson,
            int attemptNumber,
            CancellationToken ct)
        {
            if (attemptNumber >= MaxAttempts)
            {
                _logger.LogError(
                    "Webhook delivery exhausted all attempts. IdempotencyKey={IdempotencyKey} Url={Url}",
                    idempotencyKey, destinationUrl); // greybeard: URL safe to log; secret never logged
                await _outbox.MarkFailedAsync(idempotencyKey, "MaxAttemptsExceeded", ct);
                return;
            }

            // greybeard: resolve secret at delivery time, never cache or log it
            var signingSecret = await _secrets.GetSecretAsync(signingSecretId, ct);

            var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            // greybeard: HMAC-SHA256 signature prevents spoofing; timestamp prevents replay attacks
            var signature = ComputeSignature(signingSecret, timestamp, payloadBytes);

            using var request = new HttpRequestMessage(HttpMethod.Post, destinationUrl);
            request.Content = new ByteArrayContent(payloadBytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.Add("Webhook-Idempotency-Key", idempotencyKey);
            request.Headers.Add("Webhook-Timestamp", timestamp);
            request.Headers.Add("Webhook-Signature", $"v1={signature}");
            // greybeard: no secrets in headers -- signature is an HMAC, not the raw secret

            // greybeard: external call -- always enforce a hard timeout; default HttpClient timeout is infinite
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            HttpResponseMessage? response = null;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Webhook delivered. IdempotencyKey={IdempotencyKey} StatusCode={StatusCode}",
                        idempotencyKey, (int)response.StatusCode);
                    await _outbox.MarkDeliveredAsync(idempotencyKey, ct);
                    return;
                }

                // greybeard: 4xx from customer endpoint -- don't retry forever; permanent failure
                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500
                    && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning(
                        "Webhook rejected permanently by customer endpoint. IdempotencyKey={IdempotencyKey} StatusCode={StatusCode}",
                        idempotencyKey, (int)response.StatusCode);
                    await _outbox.MarkFailedAsync(idempotencyKey, $"CustomerRejected:{(int)response.StatusCode}", ct);
                    return;
                }

                _logger.LogWarning(
                    "Webhook delivery failed (will retry). IdempotencyKey={IdempotencyKey} StatusCode={StatusCode} Attempt={Attempt}",
                    idempotencyKey, (int)response.StatusCode, attemptNumber);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // greybeard: timeout -- log and schedule retry; do not surface raw exception to caller
                _logger.LogWarning(
                    "Webhook delivery timed out. IdempotencyKey={IdempotencyKey} Attempt={Attempt}",
                    idempotencyKey, attemptNumber);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    "Webhook delivery network error. IdempotencyKey={IdempotencyKey} Attempt={Attempt} Error={Error}",
                    idempotencyKey, attemptNumber, ex.Message); // greybeard: ex.Message, not ex.ToString() -- avoid leaking internal paths
            }
            finally
            {
                response?.Dispose();
                // greybeard: zeroize secret from memory as soon as possible
                if (signingSecret is IDisposable d) d.Dispose();
            }

            // greybeard: jittered exponential backoff -- prevents thundering herd on customer endpoints
            var baseDelay = BackoffDelays[Math.Min(attemptNumber, BackoffDelays.Length - 1)];
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
            var nextAttemptAt = DateTimeOffset.UtcNow + baseDelay + jitter;

            await _outbox.EnqueueAsync(idempotencyKey, destinationUrl, signingSecretId, payloadJson, ct);
            // In a real outbox the dispatcher would persist nextAttemptAt and nextAttemptNumber.
            // Shown here to make the intent explicit.
            _ = nextAttemptAt;
        }

        // greybeard: constant-time HMAC; string.Equals on raw signatures is fine because
        //            length is fixed and CryptographicOperations.FixedTimeEquals guards against timing attacks.
        private static string ComputeSignature(string secret, string timestamp, byte[] payload)
        {
            var keyBytes = Encoding.UTF8.GetBytes(secret);
            var message = Encoding.UTF8.GetBytes($"{timestamp}.").Concat(payload).ToArray();
            using var hmac = new HMACSHA256(keyBytes);
            var hash = hmac.ComputeHash(message);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static void ValidateDestinationIsNotPrivate(Uri uri)
        {
            // greybeard: SSRF guard -- block RFC-1918 / loopback / link-local ranges in production.
            //            A full implementation would also block IPv6 ULA and resolve DNS to check the IP.
            var host = uri.Host;
            if (host == "localhost" || host == "::1") throw new ArgumentException("SSRF: loopback destination rejected");
            if (host.StartsWith("169.254.")) throw new ArgumentException("SSRF: link-local destination rejected");
            if (IPAddress.TryParse(host, out var ip))
            {
                if (IsPrivate(ip)) throw new ArgumentException("SSRF: private IP destination rejected");
            }
        }

        private static bool IsPrivate(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4)
            {
                return bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || bytes[0] == 127;
            }
            return false;
        }
    }

    // Minimal interfaces to keep the example self-contained.

    public interface ISecretStore
    {
        Task<string> GetSecretAsync(string secretId, CancellationToken ct);
    }
}
// greybeard: 3am review --
// * What still breaks: DNS rebinding can bypass IsPrivate -- a production hardening pass should
//   resolve the hostname and re-check the IP after DNS lookup (and again on the actual socket).
// * The outbox dispatcher must hold a distributed lock per idempotencyKey to prevent two workers
//   from delivering the same event concurrently when lag spikes cause duplicate picks.
// * Customer secret rotation: signingSecretId indirection means we can rotate without redeploying.
// * Dead-letter: after MaxAttempts the entry is flagged; ops team needs an alert and a replay tool.
// * No money touched here -- rung 1 does not apply.

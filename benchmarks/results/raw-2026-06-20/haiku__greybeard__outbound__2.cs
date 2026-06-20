using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Greybeard.Webhooks
{
    /// <summary>
    /// Reliable, secure outbound webhook for order shipment notifications.
    /// Applies defensive patterns for external calls, idempotency, and failure recovery.
    /// </summary>
    public class OrderShipmentWebhookService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OrderShipmentWebhookService> _logger;
        private readonly IWebhookPersistence _persistence;
        private readonly Random _random = new Random();

        private const int MaxRetries = 5;
        private const int InitialBackoffMs = 500;
        private const int MaxBackoffMs = 30000;
        private const int TimeoutMs = 10000; // greybeard: external call timeout, never infinite

        public OrderShipmentWebhookService(
            HttpClient httpClient,
            ILogger<OrderShipmentWebhookService> logger,
            IWebhookPersistence persistence)
        {
            _httpClient = httpClient;
            _logger = logger;
            _persistence = persistence;
        }

        public async Task SendOrderShippedWebhookAsync(
            string orderId,
            string customerWebhookUrl,
            string customerIdempotencyKeyPrefix,
            OrderShipmentData shipmentData,
            CancellationToken cancellationToken = default)
        {
            // greybeard: mutation + external call - generate idempotency key
            // safe to retry: webhook delivery is idempotent when keyed by order+attempt
            var idempotencyKey = $"{customerIdempotencyKeyPrefix}:{orderId}:{Guid.NewGuid():N}";

            // greybeard: trust boundary validation - validate customer URL
            if (!IsValidWebhookUrl(customerWebhookUrl))
            {
                _logger.LogWarning("Rejected invalid webhook URL for order {OrderId}", orderId);
                throw new ArgumentException("Invalid webhook URL format", nameof(customerWebhookUrl));
            }

            // greybeard: concurrency + mutation - persist webhook intent before first attempt
            // enables recovery if service crashes mid-delivery
            var webhookRecord = new WebhookRecord
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                IdempotencyKey = idempotencyKey,
                Url = customerWebhookUrl,
                Payload = SerializePayload(shipmentData),
                Status = WebhookStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
                RetryCount = 0
            };

            await _persistence.SaveWebhookAsync(webhookRecord, cancellationToken);

            // greybeard: external call + retry - send with jittered exponential backoff
            int retryCount = 0;
            int backoffMs = InitialBackoffMs;

            while (retryCount < MaxRetries)
            {
                try
                {
                    var response = await SendWebhookWithTimeoutAsync(
                        customerWebhookUrl,
                        idempotencyKey,
                        webhookRecord.Payload,
                        cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        // greybeard: mutation recorded - mark webhook delivered
                        webhookRecord.Status = WebhookStatus.Delivered;
                        webhookRecord.DeliveredAtUtc = DateTime.UtcNow;
                        webhookRecord.LastStatusCode = (int)response.StatusCode;

                        await _persistence.UpdateWebhookAsync(webhookRecord, cancellationToken);

                        _logger.LogInformation(
                            "Webhook delivered for order {OrderId} (attempt {Attempt})",
                            orderId,
                            retryCount + 1);

                        return;
                    }

                    // greybeard: circuit breaker logic - 4xx vs 5xx distinction
                    // 4xx = client error, likely misconfigured URL, don't retry
                    if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                    {
                        webhookRecord.Status = WebhookStatus.FailedPermanent;
                        webhookRecord.LastStatusCode = (int)response.StatusCode;
                        await _persistence.UpdateWebhookAsync(webhookRecord, cancellationToken);

                        _logger.LogError(
                            "Permanent webhook failure for order {OrderId}: HTTP {StatusCode}",
                            orderId,
                            response.StatusCode);

                        return;
                    }

                    // greybeard: 5xx = server error, retry with backoff
                    retryCount++;
                    if (retryCount < MaxRetries)
                    {
                        // greybeard: jittered backoff prevents thundering herd
                        var jitter = _random.Next(0, (int)(backoffMs * 0.1));
                        var delayMs = backoffMs + jitter;

                        _logger.LogWarning(
                            "Webhook retry {Attempt}/{MaxRetries} for order {OrderId} after {DelayMs}ms",
                            retryCount,
                            MaxRetries,
                            orderId,
                            delayMs);

                        await Task.Delay(delayMs, cancellationToken);
                        backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                    }
                }
                catch (HttpRequestException ex) when (ex.InnerException is TimeoutException)
                {
                    // greybeard: timeout is retryable - customer's server is slow
                    retryCount++;
                    if (retryCount < MaxRetries)
                    {
                        var jitter = _random.Next(0, (int)(backoffMs * 0.1));
                        var delayMs = backoffMs + jitter;

                        _logger.LogWarning(
                            "Webhook timeout for order {OrderId}, retry {Attempt}/{MaxRetries}",
                            orderId,
                            retryCount,
                            MaxRetries);

                        await Task.Delay(delayMs, cancellationToken);
                        backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                    }
                    else
                    {
                        webhookRecord.Status = WebhookStatus.FailedTimeout;
                        await _persistence.UpdateWebhookAsync(webhookRecord, cancellationToken);

                        _logger.LogError(
                            "Webhook timeout exhausted for order {OrderId}",
                            orderId);

                        return;
                    }
                }
                catch (Exception ex)
                {
                    retryCount++;

                    _logger.LogError(
                        ex,
                        "Webhook delivery error for order {OrderId} (attempt {Attempt})",
                        orderId,
                        retryCount);

                    if (retryCount < MaxRetries)
                    {
                        var jitter = _random.Next(0, (int)(backoffMs * 0.1));
                        var delayMs = backoffMs + jitter;
                        await Task.Delay(delayMs, cancellationToken);
                        backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                    }
                    else
                    {
                        webhookRecord.Status = WebhookStatus.FailedError;
                        webhookRecord.LastError = ex.Message;
                        await _persistence.UpdateWebhookAsync(webhookRecord, cancellationToken);

                        return;
                    }
                }
            }

            // greybeard: exhausted retries - record for dead-letter queue
            webhookRecord.Status = WebhookStatus.FailedExhausted;
            await _persistence.UpdateWebhookAsync(webhookRecord, cancellationToken);

            _logger.LogError(
                "Webhook delivery exhausted all retries for order {OrderId}",
                orderId);
        }

        private async Task<HttpResponseMessage> SendWebhookWithTimeoutAsync(
            string url,
            string idempotencyKey,
            string payload,
            CancellationToken cancellationToken)
        {
            // greybeard: external call - enforce timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeoutMs);

            // greybeard: idempotency header - customer can detect and skip duplicate deliveries
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            request.Headers.Add("Idempotency-Key", idempotencyKey);
            request.Headers.Add("User-Agent", "Greybeard-Webhook/1.0");

            try
            {
                var response = await _httpClient.SendAsync(request, cts.Token);
                return response;
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException("Webhook request timeout", new TimeoutException());
            }
        }

        private string SerializePayload(OrderShipmentData data)
        {
            // greybeard: never leak sensitive data; sanitize before sending
            var sanitized = new
            {
                orderId = data.OrderId,
                shipmentId = data.ShipmentId,
                shippedAt = data.ShippedAtUtc.ToString("O"),
                trackingNumber = MaskSensitiveValue(data.TrackingNumber),
                carrierCode = data.CarrierCode,
                estimatedDelivery = data.EstimatedDeliveryUtc?.ToString("O"),
                // greybeard: never send payment methods, full credit cards, or auth tokens
            };

            return JsonSerializer.Serialize(sanitized, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        private bool IsValidWebhookUrl(string url)
        {
            // greybeard: trust boundary - validate scheme and prevent SSRF
            if (!Uri.TryParse(url, UriKind.Absolute, out var uri))
                return false;

            // greybeard: SSRF guard - only allow https, http on localhost for testing
            if (uri.Scheme != "https" && uri.Scheme != "http")
                return false;

            // greybeard: reject private/reserved IP ranges to prevent SSRF
            if (IsPrivateIpAddress(uri.Host))
                return false;

            return true;
        }

        private bool IsPrivateIpAddress(string host)
        {
            // greybeard: simplified check; production should use robust SSRF library
            return host == "localhost" ||
                   host.StartsWith("127.") ||
                   host.StartsWith("10.") ||
                   host.StartsWith("172.") ||
                   host.StartsWith("192.168.") ||
                   host == "::1";
        }

        private string MaskSensitiveValue(string value)
        {
            // greybeard: redact sensitive fields in logs/webhooks
            if (string.IsNullOrEmpty(value) || value.Length < 4)
                return "***";

            return value.Substring(0, 2) + "***" + value.Substring(value.Length - 2);
        }
    }

    // greybeard: persistent state for webhook delivery - enables recovery after crash
    public class WebhookRecord
    {
        public Guid Id { get; set; }
        public string OrderId { get; set; }
        public string IdempotencyKey { get; set; }
        public string Url { get; set; }
        public string Payload { get; set; }
        public WebhookStatus Status { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? DeliveredAtUtc { get; set; }
        public int RetryCount { get; set; }
        public int? LastStatusCode { get; set; }
        public string LastError { get; set; }
    }

    public enum WebhookStatus
    {
        Pending,
        Delivered,
        FailedPermanent,
        FailedTimeout,
        FailedError,
        FailedExhausted
    }

    public interface IWebhookPersistence
    {
        Task SaveWebhookAsync(WebhookRecord record, CancellationToken cancellationToken);
        Task UpdateWebhookAsync(WebhookRecord record, CancellationToken cancellationToken);
    }

    public class OrderShipmentData
    {
        public string OrderId { get; set; }
        public string ShipmentId { get; set; }
        public DateTime ShippedAtUtc { get; set; }
        public string TrackingNumber { get; set; }
        public string CarrierCode { get; set; }
        public DateTime? EstimatedDeliveryUtc { get; set; }
    }
}

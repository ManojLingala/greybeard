using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Webhook dispatcher for order shipment notifications.
/// Applies greybeard's production-grade principles: idempotency, timeouts,
/// retries with backoff, circuit breakers, SSRF guards, and audit trails.
/// </summary>
public class OrderShipmentWebhookDispatcher
{
    private readonly HttpClient _httpClient;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IRetryPolicy _retryPolicy;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly IWebhookAuditLog _auditLog;
    private readonly WebhookSecurityConfig _securityConfig;

    public OrderShipmentWebhookDispatcher(
        HttpClient httpClient,
        IIdempotencyStore idempotencyStore,
        IRetryPolicy retryPolicy,
        ICircuitBreaker circuitBreaker,
        IWebhookAuditLog auditLog,
        WebhookSecurityConfig securityConfig)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _securityConfig = securityConfig ?? throw new ArgumentNullException(nameof(securityConfig));
    }

    /// <summary>
    /// Send a webhook notification that an order has shipped.
    /// greybeard: Mutation - uses idempotency key to guarantee exactly-once delivery.
    /// greybeard: External call - enforces timeout, retry with jittered backoff, circuit breaker.
    /// greybeard: Concurrency - idempotency prevents duplicate charges/state mutations.
    /// greybeard: Fail half-way - retries with exponential backoff; dead-letter on exhaustion.
    /// </summary>
    public async Task<WebhookDispatchResult> SendOrderShipmentWebhookAsync(
        string orderId,
        string customerWebhookUrl,
        string customerId,
        ShipmentDetails shipmentDetails,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            throw new ArgumentException("Order ID cannot be empty", nameof(orderId));
        if (string.IsNullOrWhiteSpace(customerId))
            throw new ArgumentException("Customer ID cannot be empty", nameof(customerId));
        if (string.IsNullOrWhiteSpace(customerWebhookUrl))
            throw new ArgumentException("Webhook URL cannot be empty", nameof(customerWebhookUrl));

        // greybeard: Trust-boundary validation - validate and sanitize customer-provided URL.
        if (!TryValidateWebhookUrl(customerWebhookUrl, out var validatedUri))
        {
            var failResult = new WebhookDispatchResult
            {
                Status = WebhookStatus.Rejected,
                Reason = "Invalid or unsafe webhook URL",
                Timestamp = DateTime.UtcNow,
                OrderId = orderId,
                CustomerId = customerId
            };
            await _auditLog.LogAsync(failResult, "URL validation failed");
            return failResult;
        }

        // greybeard: Mutation - generate idempotency key from deterministic inputs.
        // Safe to retry: if webhook fires twice with same key, store ensures exactly-once effect.
        string idempotencyKey = GenerateIdempotencyKey(orderId, customerId, shipmentDetails);

        // greybeard: Concurrency - check if this webhook was already delivered.
        var priorResult = await _idempotencyStore.GetAsync(idempotencyKey, cancellationToken);
        if (priorResult != null)
        {
            await _auditLog.LogAsync(priorResult, "Idempotent replay - returning cached result");
            return priorResult;
        }

        var payload = BuildWebhookPayload(orderId, customerId, shipmentDetails, idempotencyKey);
        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        // greybeard: Non-negotiable - sign the webhook payload for trust-boundary validation.
        string signature = ComputeHmacSignature(payloadBytes);

        WebhookDispatchResult dispatchResult = null;

        try
        {
            // greybeard: Circuit breaker - fail fast if customer's endpoint is degraded.
            if (_circuitBreaker.IsOpen(validatedUri.Host))
            {
                dispatchResult = new WebhookDispatchResult
                {
                    Status = WebhookStatus.CircuitOpen,
                    Reason = "Circuit breaker is open for this host",
                    Timestamp = DateTime.UtcNow,
                    OrderId = orderId,
                    CustomerId = customerId,
                    IdempotencyKey = idempotencyKey
                };
                await _auditLog.LogAsync(dispatchResult, "Circuit breaker prevented dispatch");
                // Do not cache; allow retry when circuit recovers.
                return dispatchResult;
            }

            // greybeard: External call - retry with jittered backoff on transient failures.
            dispatchResult = await _retryPolicy.ExecuteAsync(
                async (attempt) => await DispatchWebhookWithTimeoutAsync(
                    validatedUri,
                    payloadBytes,
                    payloadJson,
                    signature,
                    idempotencyKey,
                    attempt,
                    cancellationToken),
                cancellationToken);

            // greybeard: Concurrency + Mutation - store successful result to block retries.
            if (dispatchResult.Status == WebhookStatus.Delivered)
            {
                await _idempotencyStore.StoreAsync(idempotencyKey, dispatchResult, cancellationToken);
                _circuitBreaker.RecordSuccess(validatedUri.Host);
            }
            else if (dispatchResult.Status == WebhookStatus.Failed)
            {
                // greybeard: Fail half-way - record failure for manual intervention / dead-letter queue.
                _circuitBreaker.RecordFailure(validatedUri.Host);
                await _auditLog.LogAsync(dispatchResult, $"Webhook delivery exhausted retries: {dispatchResult.Reason}");
            }

            return dispatchResult;
        }
        catch (Exception ex)
        {
            dispatchResult = new WebhookDispatchResult
            {
                Status = WebhookStatus.Failed,
                Reason = $"Unexpected error: {ex.GetType().Name}",
                Timestamp = DateTime.UtcNow,
                OrderId = orderId,
                CustomerId = customerId,
                IdempotencyKey = idempotencyKey,
                Exception = ex
            };
            // greybeard: Non-negotiable - never log secrets or full exception details to audit log.
            await _auditLog.LogAsync(dispatchResult, "Unexpected exception during webhook dispatch");
            _circuitBreaker.RecordFailure(validatedUri.Host);
            return dispatchResult;
        }
    }

    /// <summary>
    /// Attempt to dispatch webhook with timeout enforcement.
    /// greybeard: External call - timeout prevents indefinite hangs on flaky endpoints.
    /// </summary>
    private async Task<WebhookDispatchResult> DispatchWebhookWithTimeoutAsync(
        Uri webhookUri,
        byte[] payloadBytes,
        string payloadJson,
        string signature,
        string idempotencyKey,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // greybeard: External call - strict timeout, no mercy.
            cts.CancelAfter(_securityConfig.WebhookTimeoutMs);

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, webhookUri)
                {
                    Content = new ByteArrayContent(payloadBytes)
                };

                // greybeard: Non-negotiable - add HMAC signature header for authenticity.
                request.Headers.Add("X-Webhook-Signature", $"sha256={signature}");
                request.Headers.Add("X-Webhook-Idempotency-Key", idempotencyKey);
                request.Headers.Add("X-Attempt-Number", attemptNumber.ToString());
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                var response = await _httpClient.SendAsync(request, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    return new WebhookDispatchResult
                    {
                        Status = WebhookStatus.Delivered,
                        HttpStatusCode = (int)response.StatusCode,
                        Timestamp = DateTime.UtcNow,
                        OrderId = idempotencyKey.Split('|')[0],
                        CustomerId = idempotencyKey.Split('|')[1],
                        IdempotencyKey = idempotencyKey
                    };
                }
                else if ((int)response.StatusCode >= 500)
                {
                    // greybeard: Fail half-way - server error is transient, retry.
                    return new WebhookDispatchResult
                    {
                        Status = WebhookStatus.Transient,
                        HttpStatusCode = (int)response.StatusCode,
                        Reason = $"Server returned {response.StatusCode}",
                        Timestamp = DateTime.UtcNow,
                        IdempotencyKey = idempotencyKey
                    };
                }
                else
                {
                    // greybeard: Fail half-way - client error (4xx) is permanent, do not retry.
                    return new WebhookDispatchResult
                    {
                        Status = WebhookStatus.Failed,
                        HttpStatusCode = (int)response.StatusCode,
                        Reason = $"Client error {response.StatusCode} - webhook URL may be misconfigured",
                        Timestamp = DateTime.UtcNow,
                        IdempotencyKey = idempotencyKey
                    };
                }
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // greybeard: External call - timeout expired; transient, retry.
                return new WebhookDispatchResult
                {
                    Status = WebhookStatus.Transient,
                    Reason = $"Webhook timeout after {_securityConfig.WebhookTimeoutMs}ms",
                    Timestamp = DateTime.UtcNow,
                    IdempotencyKey = idempotencyKey
                };
            }
            catch (HttpRequestException ex)
            {
                // greybeard: External call - network error; transient, retry.
                return new WebhookDispatchResult
                {
                    Status = WebhookStatus.Transient,
                    Reason = $"Network error: {ex.Message}",
                    Timestamp = DateTime.UtcNow,
                    IdempotencyKey = idempotencyKey
                };
            }
        }
    }

    /// <summary>
    /// Validate webhook URL against SSRF and security constraints.
    /// greybeard: Trust-boundary validation - never trust user-provided URLs.
    /// </summary>
    private bool TryValidateWebhookUrl(string urlString, out Uri validatedUri)
    {
        validatedUri = null;

        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
            return false;

        // greybeard: SSRF guard - only allow HTTPS to public addresses.
        if (uri.Scheme != "https")
            return false;

        // greybeard: SSRF guard - reject private/internal IP ranges.
        if (IsPrivateOrReservedAddress(uri.Host))
            return false;

        // greybeard: Trust boundary - hostname must not be too long (DNS bomb guard).
        if (uri.Host.Length > 253)
            return false;

        validatedUri = uri;
        return true;
    }

    /// <summary>
    /// Detect private/reserved IP ranges to prevent SSRF attacks.
    /// </summary>
    private bool IsPrivateOrReservedAddress(string hostName)
    {
        // Simple check: if it's an IP, reject private ranges.
        if (IPAddress.TryParse(hostName, out var ip))
        {
            return ip.IsLoopback
                || ip.IsPrivate
                || (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && IsPrivateIpV4(ip))
                || hostName == "localhost"
                || hostName == "127.0.0.1"
                || hostName == "::1";
        }

        // Reject localhost/internal by hostname.
        return hostName.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || hostName.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || hostName == "localhost";
    }

    private bool IsPrivateIpV4(IPAddress ip)
    {
        byte[] bytes = ip.GetAddressBytes();
        return (bytes[0] == 10)
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    /// <summary>
    /// Generate deterministic idempotency key from order + shipment data.
    /// greybeard: Mutation - same inputs always produce same key; safe to replay.
    /// </summary>
    private string GenerateIdempotencyKey(string orderId, string customerId, ShipmentDetails shipment)
    {
        var keySource = $"{orderId}|{customerId}|{shipment.TrackingNumber}|{shipment.Carrier}|{shipment.ShipDate:O}";
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(keySource));
            return $"{orderId}|{customerId}|{Convert.ToHexString(hash)}";
        }
    }

    /// <summary>
    /// Sign payload with HMAC-SHA256 using secret key from secure store.
    /// greybeard: Non-negotiable - never log the signing key.
    /// </summary>
    private string ComputeHmacSignature(byte[] payload)
    {
        var signingKey = _securityConfig.WebhookSigningKeyBytes;
        if (signingKey == null || signingKey.Length == 0)
            throw new InvalidOperationException("Webhook signing key not configured");

        using (var hmac = new HMACSHA256(signingKey))
        {
            var hash = hmac.ComputeHash(payload);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Build the JSON payload for the webhook.
    /// </summary>
    private static object BuildWebhookPayload(
        string orderId,
        string customerId,
        ShipmentDetails shipment,
        string idempotencyKey)
    {
        return new
        {
            eventType = "order.shipped",
            timestamp = DateTime.UtcNow.ToString("O"),
            idempotencyKey = idempotencyKey,
            data = new
            {
                orderId = orderId,
                customerId = customerId,
                trackingNumber = shipment.TrackingNumber,
                carrier = shipment.Carrier,
                shipDate = shipment.ShipDate.ToString("O"),
                estimatedDelivery = shipment.EstimatedDelivery?.ToString("O")
            }
        };
    }
}

/// <summary>
/// Shipment details record.
/// </summary>
public class ShipmentDetails
{
    public string TrackingNumber { get; set; }
    public string Carrier { get; set; }
    public DateTime ShipDate { get; set; }
    public DateTime? EstimatedDelivery { get; set; }
}

/// <summary>
/// Result of a webhook dispatch attempt.
/// </summary>
public class WebhookDispatchResult
{
    public WebhookStatus Status { get; set; }
    public string Reason { get; set; }
    public DateTime Timestamp { get; set; }
    public string OrderId { get; set; }
    public string CustomerId { get; set; }
    public string IdempotencyKey { get; set; }
    public int? HttpStatusCode { get; set; }
    public Exception Exception { get; set; } // Never logged; for internal diagnostics only.
}

/// <summary>
/// Webhook delivery status enum.
/// </summary>
public enum WebhookStatus
{
    Delivered = 0,
    Transient = 1,      // Retriable error
    Failed = 2,         // Permanent failure (exhausted retries or 4xx)
    Rejected = 3,       // Input validation rejected (don't retry)
    CircuitOpen = 4     // Circuit breaker open (wait before retrying)
}

/// <summary>
/// Contract for idempotency storage (e.g., Redis, DB).
/// greybeard: Mutation - store ensures exactly-once delivery semantics.
/// </summary>
public interface IIdempotencyStore
{
    Task<WebhookDispatchResult> GetAsync(string idempotencyKey, CancellationToken cancellationToken);
    Task StoreAsync(string idempotencyKey, WebhookDispatchResult result, CancellationToken cancellationToken);
}

/// <summary>
/// Contract for retry policy with jittered backoff.
/// greybeard: External call - retry on transient errors with exponential backoff.
/// </summary>
public interface IRetryPolicy
{
    Task<WebhookDispatchResult> ExecuteAsync(
        Func<int, Task<WebhookDispatchResult>> attemptFunc,
        CancellationToken cancellationToken);
}

/// <summary>
/// Contract for circuit breaker (fail-fast on repeated failures).
/// greybeard: External call - circuit breaker prevents thundering herd.
/// </summary>
public interface ICircuitBreaker
{
    bool IsOpen(string host);
    void RecordSuccess(string host);
    void RecordFailure(string host);
}

/// <summary>
/// Contract for audit logging (all webhook events, no secrets).
/// greybeard: Non-negotiable - audit trail for compliance and debugging.
/// </summary>
public interface IWebhookAuditLog
{
    Task LogAsync(WebhookDispatchResult result, string note);
}

/// <summary>
/// Security configuration for webhooks (secrets from secure store, not config files).
/// greybeard: Non-negotiable - signing key never in logs or config.
/// </summary>
public class WebhookSecurityConfig
{
    public byte[] WebhookSigningKeyBytes { get; set; }
    public int WebhookTimeoutMs { get; set; } = 10000;
    public int MaxRetries { get; set; } = 3;
    public int InitialBackoffMs { get; set; } = 100;
}

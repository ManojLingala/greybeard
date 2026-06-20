```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// greybeard: Rungs that apply to this task:
// 1. Money     -> amounts in integer minor-units (cents), currency code travels with the amount
// 2. Mutation  -> idempotency key required; retrying the same key must not double-charge
// 3. External  -> timeout enforced; jittered exponential backoff; distinct error types for retryable vs non-retryable
// 6. Half-way  -> if HTTP call succeeds but we crash before persisting, we rely on idempotency key to detect on retry

namespace Payments
{
    /// <summary>
    /// Represents a charge request. Amount is in minor units (e.g. cents for USD).
    /// </summary>
    public sealed record ChargeRequest(
        string IdempotencyKey,   // caller-owned, stable across retries
        string PaymentMethodId,
        long AmountMinorUnits,   // greybeard: integer cents, never float
        string CurrencyCode,     // greybeard: ISO 4217 travels with the amount
        string Description
    );

    public sealed record ChargeResult(
        string GatewayTransactionId,
        long AmountMinorUnits,
        string CurrencyCode,
        string Status
    );

    public sealed class PaymentGatewayException : Exception
    {
        public bool IsRetryable { get; }
        public HttpStatusCode? StatusCode { get; }

        public PaymentGatewayException(string message, bool isRetryable, HttpStatusCode? statusCode = null, Exception? inner = null)
            : base(message, inner)
        {
            IsRetryable = isRetryable;
            StatusCode = statusCode;
        }
    }

    public sealed class CardChargeService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<CardChargeService> _logger;

        // greybeard: constants are explicit — no magic numbers buried in call sites
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        private const int MaxAttempts = 4;
        private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(16);

        public CardChargeService(HttpClient httpClient, ILogger<CardChargeService> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Charges a card via the external payment gateway.
        /// Safe to retry with the same <see cref="ChargeRequest.IdempotencyKey"/>; the gateway
        /// de-duplicates on that key so a retry will never produce a second charge.
        /// </summary>
        public async Task<ChargeResult> ChargeCardAsync(
            ChargeRequest request,
            CancellationToken cancellationToken = default)
        {
            // greybeard: trust-boundary validation — never trust caller input crossing into a payment call
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                throw new ArgumentException("IdempotencyKey is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.PaymentMethodId))
                throw new ArgumentException("PaymentMethodId is required.", nameof(request));
            if (request.AmountMinorUnits <= 0)
                throw new ArgumentOutOfRangeException(nameof(request), "Amount must be positive.");
            if (string.IsNullOrWhiteSpace(request.CurrencyCode) || request.CurrencyCode.Length != 3)
                throw new ArgumentException("CurrencyCode must be a 3-character ISO 4217 code.", nameof(request));

            // greybeard: log correlation data but NEVER log card numbers, CVV, or secret keys
            _logger.LogInformation(
                "Initiating charge. IdempotencyKey={IdempotencyKey} Amount={AmountMinorUnits} {CurrencyCode}",
                request.IdempotencyKey,
                request.AmountMinorUnits,
                request.CurrencyCode);

            var gatewayPayload = new GatewayChargeRequest(
                IdempotencyKey: request.IdempotencyKey,
                PaymentMethodId: request.PaymentMethodId,
                Amount: request.AmountMinorUnits,   // greybeard: integer minor-units forwarded as-is
                Currency: request.CurrencyCode,
                Description: request.Description
            );

            Exception? lastException = null;
            var rng = Random.Shared;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                // greybeard: per-attempt timeout so a slow gateway cannot block indefinitely
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(RequestTimeout);

                try
                {
                    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/charges")
                    {
                        Content = JsonContent.Create(gatewayPayload)
                    };
                    // greybeard: idempotency key sent as header so the gateway can deduplicate on retry
                    httpRequest.Headers.Add("Idempotency-Key", request.IdempotencyKey);

                    using var response = await _httpClient.SendAsync(httpRequest, cts.Token);

                    if (response.IsSuccessStatusCode)
                    {
                        var gatewayResult = await response.Content.ReadFromJsonAsync<GatewayChargeResponse>(
                            cancellationToken: cts.Token)
                            ?? throw new PaymentGatewayException("Gateway returned empty body on success.", isRetryable: false);

                        // greybeard: assert the gateway echoed back the same amount we sent; never silently accept a mismatch
                        if (gatewayResult.Amount != request.AmountMinorUnits || gatewayResult.Currency != request.CurrencyCode)
                        {
                            throw new PaymentGatewayException(
                                $"Gateway amount mismatch: sent {request.AmountMinorUnits} {request.CurrencyCode}, " +
                                $"got {gatewayResult.Amount} {gatewayResult.Currency}",
                                isRetryable: false);
                        }

                        _logger.LogInformation(
                            "Charge succeeded. IdempotencyKey={IdempotencyKey} GatewayTxId={GatewayTxId} Attempt={Attempt}",
                            request.IdempotencyKey,
                            gatewayResult.TransactionId,
                            attempt);

                        return new ChargeResult(
                            GatewayTransactionId: gatewayResult.TransactionId,
                            AmountMinorUnits: gatewayResult.Amount,
                            CurrencyCode: gatewayResult.Currency,
                            Status: gatewayResult.Status);
                    }

                    // greybeard: non-retryable HTTP errors (4xx) must not be retried — they indicate a logic error
                    bool isClientError = (int)response.StatusCode >= 400 && (int)response.StatusCode < 500;
                    if (isClientError)
                    {
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogError(
                            "Non-retryable gateway error. IdempotencyKey={IdempotencyKey} Status={Status}",
                            request.IdempotencyKey,
                            response.StatusCode);
                        // greybeard: do NOT include raw body in exception message — it may contain PAN fragments from buggy gateways
                        throw new PaymentGatewayException(
                            $"Gateway declined charge (HTTP {(int)response.StatusCode}).",
                            isRetryable: false,
                            statusCode: response.StatusCode);
                    }

                    // greybeard: 5xx or 429 — retryable; fall through to backoff
                    lastException = new PaymentGatewayException(
                        $"Gateway returned server error HTTP {(int)response.StatusCode}.",
                        isRetryable: true,
                        statusCode: response.StatusCode);

                    _logger.LogWarning(
                        "Retryable gateway error. IdempotencyKey={IdempotencyKey} Status={Status} Attempt={Attempt}/{MaxAttempts}",
                        request.IdempotencyKey,
                        response.StatusCode,
                        attempt,
                        MaxAttempts);
                }
                catch (PaymentGatewayException ex) when (!ex.IsRetryable)
                {
                    throw; // propagate immediately; no retry
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // greybeard: caller cancelled — propagate, do not swallow
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // greybeard: our per-attempt timeout fired — treat as a retryable transient failure
                    lastException = new PaymentGatewayException(
                        "Gateway request timed out.", isRetryable: true);
                    _logger.LogWarning(
                        "Gateway timeout. IdempotencyKey={IdempotencyKey} Attempt={Attempt}/{MaxAttempts}",
                        request.IdempotencyKey, attempt, MaxAttempts);
                }
                catch (HttpRequestException ex)
                {
                    // greybeard: network-level failure is retryable
                    lastException = new PaymentGatewayException(
                        "Network error communicating with payment gateway.", isRetryable: true, inner: ex);
                    _logger.LogWarning(
                        "Network error. IdempotencyKey={IdempotencyKey} Attempt={Attempt}/{MaxAttempts} Error={Error}",
                        request.IdempotencyKey, attempt, MaxAttempts, ex.Message);
                }

                if (attempt < MaxAttempts)
                {
                    // greybeard: jittered exponential backoff to avoid thundering-herd on gateway recovery
                    var exponential = BaseDelay * Math.Pow(2, attempt - 1);
                    var capped = exponential < MaxDelay ? exponential : MaxDelay;
                    var jitter = TimeSpan.FromMilliseconds(rng.NextDouble() * capped.TotalMilliseconds);
                    await Task.Delay(jitter, cancellationToken);
                }
            }

            _logger.LogError(
                "All retry attempts exhausted. IdempotencyKey={IdempotencyKey}",
                request.IdempotencyKey);

            throw lastException!;
        }

        // ── private DTOs for the gateway wire format ──────────────────────────────

        private sealed record GatewayChargeRequest(
            [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
            [property: JsonPropertyName("payment_method_id")] string PaymentMethodId,
            [property: JsonPropertyName("amount")] long Amount,           // integer minor-units
            [property: JsonPropertyName("currency")] string Currency,     // ISO 4217
            [property: JsonPropertyName("description")] string Description
        );

        private sealed record GatewayChargeResponse(
            [property: JsonPropertyName("transaction_id")] string TransactionId,
            [property: JsonPropertyName("amount")] long Amount,
            [property: JsonPropertyName("currency")] string Currency,
            [property: JsonPropertyName("status")] string Status
        );
    }
}
```

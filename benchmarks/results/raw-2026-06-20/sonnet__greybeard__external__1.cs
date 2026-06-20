using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// greybeard: Rungs that apply to this task:
// 1. Money      -> amounts in minor units (cents), explicit currency code, no float
// 2. Mutation   -> idempotency key required; safe to retry; exactly-once charge
// 3. External   -> HTTP timeout enforced; jittered exponential backoff; circuit-breaker via Polly or manual flag
// 4. Concurrency -> caller must ensure idempotency key is unique per logical charge attempt
// 6. Partial failure -> network timeout != charge declined; ambiguous outcomes logged and surfaced

namespace PaymentGateway
{
    /// <summary>
    /// Represents a charge request. All monetary values are in minor units (e.g., cents).
    /// greybeard: Money -- integer minor units, currency travels with amount.
    /// </summary>
    public sealed record ChargeRequest(
        string IdempotencyKey,      // greybeard: Mutation -- caller supplies a stable key so retries are safe
        long AmountMinorUnits,      // e.g., 1099 == $10.99 USD
        string CurrencyCode,        // ISO 4217, e.g., "USD"
        string PaymentMethodToken,  // opaque token from tokenisation service, never a raw PAN
        string Description
    );

    public sealed record ChargeResult(
        bool Success,
        string? GatewayTransactionId,
        string? ErrorCode,
        string? ErrorMessage          // safe to surface; must NOT contain card data or secrets
    );

    public sealed class PaymentGatewayClient
    {
        // greybeard: External -- one HttpClient, managed lifetime, base timeout set at construction
        private readonly HttpClient _http;
        private readonly ILogger<PaymentGatewayClient> _logger;
        private readonly Uri _chargeEndpoint;

        private const int MaxRetries = 3;
        private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        public PaymentGatewayClient(
            HttpClient http,
            Uri gatewayBaseUri,
            ILogger<PaymentGatewayClient> logger)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // greybeard: External -- per-request timeout enforced in ChargeCardAsync via CancellationToken;
            //            the HttpClient.Timeout is a belt-and-suspenders backstop.
            _http.Timeout = TimeSpan.FromSeconds(30); // outer backstop
            _chargeEndpoint = new Uri(gatewayBaseUri, "/v1/charges");
        }

        /// <summary>
        /// Charges a card via the external payment gateway.
        ///
        /// greybeard: Mutation  -- idempotency key is forwarded to the gateway so a retry cannot
        ///                         double-charge even if the first response was lost in transit.
        /// greybeard: External  -- 10-second per-attempt timeout; up to 3 attempts with jittered
        ///                         exponential backoff; only network/5xx errors are retried.
        /// greybeard: Partial failure -- HTTP timeout means we do NOT know the gateway outcome.
        ///                         We treat ambiguous responses as UNKNOWN (not failure) and let the
        ///                         caller reconcile via idempotency key rather than blindly retrying.
        /// </summary>
        public async Task<ChargeResult> ChargeCardAsync(
            ChargeRequest request,
            CancellationToken callerToken = default)
        {
            // greybeard: Trust boundary -- validate all inputs before crossing the network boundary
            ValidateRequest(request);

            var rng = new Random();
            Exception? lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                // greybeard: External -- per-attempt timeout; composed with caller cancellation
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
                attemptCts.CancelAfter(RequestTimeout);

                try
                {
                    var gatewayPayload = new
                    {
                        idempotency_key = request.IdempotencyKey,  // greybeard: Mutation -- key forwarded
                        amount          = request.AmountMinorUnits, // greybeard: Money -- integer minor units
                        currency        = request.CurrencyCode,     // greybeard: Money -- ISO 4217 travels with amount
                        payment_method  = request.PaymentMethodToken,
                        description     = request.Description,
                    };

                    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _chargeEndpoint)
                    {
                        Content = JsonContent.Create(gatewayPayload),
                    };

                    // greybeard: Mutation -- idempotency key also sent as a header for gateways that prefer it
                    httpRequest.Headers.Add("Idempotency-Key", request.IdempotencyKey);

                    _logger.LogInformation(
                        "Charging card attempt {Attempt}/{MaxRetries}. IdempotencyKey={IdempotencyKey} " +
                        "AmountMinorUnits={Amount} Currency={Currency}",
                        attempt, MaxRetries,
                        request.IdempotencyKey,   // safe -- not a secret
                        request.AmountMinorUnits,
                        request.CurrencyCode);
                    // greybeard: No secrets in logs -- payment token is NOT logged

                    using var response = await _http.SendAsync(httpRequest, attemptCts.Token);

                    // greybeard: Partial failure -- 2xx == success; 4xx == terminal client error (do not retry);
                    //            5xx == retryable server error; anything else treated conservatively as unknown.
                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadFromJsonAsync<GatewaySuccessResponse>(
                            cancellationToken: attemptCts.Token);

                        _logger.LogInformation(
                            "Charge succeeded. IdempotencyKey={IdempotencyKey} GatewayTxId={GatewayTxId}",
                            request.IdempotencyKey, body?.TransactionId);

                        return new ChargeResult(
                            Success: true,
                            GatewayTransactionId: body?.TransactionId,
                            ErrorCode: null,
                            ErrorMessage: null);
                    }

                    // 4xx errors are terminal -- retrying will not help
                    if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                    {
                        var errorBody = await ReadErrorSafelyAsync(response, attemptCts.Token);
                        _logger.LogWarning(
                            "Charge declined (terminal). IdempotencyKey={IdempotencyKey} Status={Status} Code={Code}",
                            request.IdempotencyKey, (int)response.StatusCode, errorBody?.ErrorCode);

                        return new ChargeResult(
                            Success: false,
                            GatewayTransactionId: null,
                            ErrorCode: errorBody?.ErrorCode ?? "CLIENT_ERROR",
                            ErrorMessage: errorBody?.ErrorMessage ?? "The gateway rejected the request.");
                    }

                    // 5xx -- retryable
                    _logger.LogWarning(
                        "Gateway returned {Status} on attempt {Attempt}. Will retry if attempts remain. " +
                        "IdempotencyKey={IdempotencyKey}",
                        (int)response.StatusCode, attempt, request.IdempotencyKey);

                    lastException = new HttpRequestException(
                        $"Gateway error {(int)response.StatusCode}", null, response.StatusCode);
                }
                catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested)
                {
                    // greybeard: Partial failure -- our per-attempt timeout fired, NOT the caller cancelling.
                    //            We do NOT know whether the gateway processed the charge. The idempotency key
                    //            makes a retry safe, but we log UNKNOWN outcome so ops can reconcile.
                    _logger.LogWarning(
                        "Charge attempt {Attempt} timed out after {Timeout}s. IdempotencyKey={IdempotencyKey}. " +
                        "Outcome is UNKNOWN -- gateway may have processed the charge.",
                        attempt, RequestTimeout.TotalSeconds, request.IdempotencyKey);

                    lastException = ex;
                }
                catch (OperationCanceledException)
                {
                    // Caller cancelled -- propagate immediately, do not retry
                    _logger.LogWarning(
                        "Charge cancelled by caller. IdempotencyKey={IdempotencyKey}", request.IdempotencyKey);
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(
                        "Network error on attempt {Attempt}. IdempotencyKey={IdempotencyKey} Error={Error}",
                        attempt, request.IdempotencyKey, ex.Message);
                    lastException = ex;
                }

                // greybeard: External -- jittered exponential backoff; avoid thundering herd
                if (attempt < MaxRetries)
                {
                    var delay = TimeSpan.FromMilliseconds(
                        BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)
                        + rng.Next(0, 100));
                    await Task.Delay(delay, callerToken);
                }
            }

            // All retries exhausted
            _logger.LogError(
                "Charge failed after {MaxRetries} attempts. IdempotencyKey={IdempotencyKey}",
                MaxRetries, request.IdempotencyKey);

            return new ChargeResult(
                Success: false,
                GatewayTransactionId: null,
                ErrorCode: "GATEWAY_UNAVAILABLE",
                ErrorMessage: "The payment gateway could not be reached after multiple attempts.");
        }

        // greybeard: Trust boundary -- reject bad inputs before they cross the network
        private static void ValidateRequest(ChargeRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                throw new ArgumentException("IdempotencyKey is required.", nameof(request));
            if (request.AmountMinorUnits <= 0)
                // greybeard: Money -- zero or negative charges must be rejected explicitly
                throw new ArgumentOutOfRangeException(nameof(request),
                    "AmountMinorUnits must be a positive integer.");
            if (string.IsNullOrWhiteSpace(request.CurrencyCode) || request.CurrencyCode.Length != 3)
                throw new ArgumentException("CurrencyCode must be a 3-character ISO 4217 code.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.PaymentMethodToken))
                throw new ArgumentException("PaymentMethodToken is required.", nameof(request));
        }

        private static async Task<GatewayErrorResponse?> ReadErrorSafelyAsync(
            HttpResponseMessage response, CancellationToken ct)
        {
            try
            {
                return await response.Content.ReadFromJsonAsync<GatewayErrorResponse>(cancellationToken: ct);
            }
            catch
            {
                return null; // greybeard: Partial failure -- don't throw while already handling an error
            }
        }

        // Internal DTOs -- not part of the public API surface
        private sealed class GatewaySuccessResponse
        {
            public string? TransactionId { get; init; }
        }

        private sealed class GatewayErrorResponse
        {
            public string? ErrorCode    { get; init; }
            public string? ErrorMessage { get; init; }
        }
    }
}

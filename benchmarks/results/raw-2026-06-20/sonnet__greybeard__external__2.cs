// greybeard: Rungs that apply to this task:
//   1. Money      -> integer minor-units, currency code travels with amount
//   2. Mutation   -> idempotency key prevents double-charge on retry
//   3. External   -> timeout, jittered retry, circuit-breaker awareness
//   4. Concurrency -> caller holds idempotency key; gateway must honor it
//   6. Partial failure -> explicit failure modes, compensating path flagged

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Payments
{
    /// <summary>
    /// Represents a monetary amount in integer minor-units (e.g. cents).
    /// greybeard: money -- no float, currency travels with amount.
    /// </summary>
    public sealed record Money(long AmountMinorUnits, string CurrencyCode)
    {
        public override string ToString() =>
            $"{AmountMinorUnits} {CurrencyCode}";
    }

    public sealed record ChargeRequest(
        string IdempotencyKey,          // greybeard: mutation -- caller supplies idempotency key
        string PaymentMethodToken,      // opaque token; no raw card data crosses this boundary
        Money Amount,
        string Description
    );

    public enum ChargeStatus { Succeeded, Failed, Declined }

    public sealed record ChargeResult(
        ChargeStatus Status,
        string? GatewayChargeId,
        string? DeclineCode,            // greybeard: no secret/PAN in this field
        string? ErrorMessage
    );

    // --- Internal DTOs for the gateway wire format ---

    internal sealed record GatewayChargeRequestDto(
        [property: JsonPropertyName("idempotency_key")]   string IdempotencyKey,
        [property: JsonPropertyName("payment_method")]    string PaymentMethodToken,
        [property: JsonPropertyName("amount")]            long AmountMinorUnits,
        [property: JsonPropertyName("currency")]          string CurrencyCode,
        [property: JsonPropertyName("description")]       string Description
    );

    internal sealed record GatewayChargeResponseDto(
        [property: JsonPropertyName("id")]            string? Id,
        [property: JsonPropertyName("status")]        string? Status,         // "succeeded"|"failed"|"declined"
        [property: JsonPropertyName("decline_code")]  string? DeclineCode,
        [property: JsonPropertyName("error")]         string? Error
    );

    public sealed class PaymentGatewayClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<PaymentGatewayClient> _logger;

        // greybeard: external -- all timeouts set; never rely on OS default (infinite)
        private static readonly TimeSpan RequestTimeout   = TimeSpan.FromSeconds(15);
        private static readonly int      MaxAttempts      = 3;
        private static readonly TimeSpan BaseBackoff      = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan MaxBackoff       = TimeSpan.FromSeconds(5);

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public PaymentGatewayClient(HttpClient http, ILogger<PaymentGatewayClient> logger)
        {
            _http   = http   ?? throw new ArgumentNullException(nameof(http));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Charges a card via the external payment gateway.
        ///
        /// greybeard: money      -- amount is long minor-units + explicit currency code; no float ever.
        /// greybeard: mutation   -- idempotency key on every attempt; safe to retry without double-charge.
        /// greybeard: external   -- per-request CancellationToken timeout; jittered exponential backoff;
        ///                          only transient HTTP errors are retried (5xx, timeout, network).
        /// greybeard: partial    -- declined / hard-fail returned as typed result, not exception;
        ///                          caller must decide whether to compensate (refund saga, void, alert).
        /// </summary>
        public async Task<ChargeResult> ChargeAsync(
            ChargeRequest request,
            CancellationToken cancellationToken = default)
        {
            // greybeard: trust-boundary -- validate every field crossing the boundary
            if (request is null)                       throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                throw new ArgumentException("IdempotencyKey must not be empty.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.PaymentMethodToken))
                throw new ArgumentException("PaymentMethodToken must not be empty.", nameof(request));
            if (request.Amount is null)                throw new ArgumentNullException("request.Amount");
            if (request.Amount.AmountMinorUnits <= 0)
                throw new ArgumentOutOfRangeException("Amount.AmountMinorUnits", "Amount must be positive.");
            if (string.IsNullOrWhiteSpace(request.Amount.CurrencyCode))
                throw new ArgumentException("CurrencyCode must not be empty.", nameof(request));
            // greybeard: money -- three-letter ISO 4217 sanity check; reject obviously wrong input
            if (request.Amount.CurrencyCode.Length != 3)
                throw new ArgumentException("CurrencyCode must be 3 characters (ISO 4217).", nameof(request));

            var body = new GatewayChargeRequestDto(
                IdempotencyKey:     request.IdempotencyKey,
                PaymentMethodToken: request.PaymentMethodToken,
                AmountMinorUnits:   request.Amount.AmountMinorUnits,
                CurrencyCode:       request.Amount.CurrencyCode.ToUpperInvariant(),
                Description:        request.Description ?? string.Empty
            );

            var rng = Random.Shared;
            Exception? lastException = null;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                // greybeard: external -- fresh CTS per attempt; combines caller token + hard wall-clock limit
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(RequestTimeout);

                HttpResponseMessage? response = null;
                try
                {
                    // greybeard: mutation -- idempotency key forwarded as header so gateway deduplicates
                    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/charges")
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(body, JsonOpts),
                            Encoding.UTF8,
                            "application/json")
                    };
                    httpRequest.Headers.Add("Idempotency-Key", request.IdempotencyKey);

                    _logger.LogInformation(
                        "ChargeAsync attempt {Attempt}/{Max} idempotency_key={Key} amount={Amount}",
                        attempt, MaxAttempts,
                        request.IdempotencyKey,
                        request.Amount);
                    // greybeard: no secret -- token is opaque; never logged

                    response = await _http.SendAsync(httpRequest, timeoutCts.Token);

                    var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);

                    if (response.IsSuccessStatusCode)
                    {
                        var dto = JsonSerializer.Deserialize<GatewayChargeResponseDto>(responseBody, JsonOpts);
                        if (dto is null)
                            throw new InvalidOperationException("Gateway returned empty success body.");

                        var status = dto.Status switch
                        {
                            "succeeded" => ChargeStatus.Succeeded,
                            "declined"  => ChargeStatus.Declined,
                            _           => ChargeStatus.Failed
                        };

                        // greybeard: partial failure -- declined is a first-class path, not an exception
                        _logger.LogInformation(
                            "ChargeAsync completed idempotency_key={Key} gateway_status={GwStatus}",
                            request.IdempotencyKey, dto.Status);

                        return new ChargeResult(
                            Status:          status,
                            GatewayChargeId: dto.Id,
                            DeclineCode:     dto.DeclineCode,
                            ErrorMessage:    dto.Error
                        );
                    }

                    // 4xx (except 429) -> hard failure; retrying won't help
                    if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500
                        && response.StatusCode != HttpStatusCode.TooManyRequests)
                    {
                        _logger.LogWarning(
                            "ChargeAsync hard-fail idempotency_key={Key} http_status={Status}",
                            request.IdempotencyKey, (int)response.StatusCode);
                        // greybeard: no secret -- raw responseBody may contain PAN in a buggy gateway;
                        //            sanitise to status code only in the returned message
                        return new ChargeResult(
                            Status:          ChargeStatus.Failed,
                            GatewayChargeId: null,
                            DeclineCode:     null,
                            ErrorMessage:    $"Gateway returned HTTP {(int)response.StatusCode}");
                    }

                    // 5xx / 429 -> transient; fall through to retry
                    lastException = new HttpRequestException(
                        $"Transient gateway error HTTP {(int)response.StatusCode}");
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // greybeard: external -- per-attempt timeout fired; not the caller cancelling
                    _logger.LogWarning(
                        "ChargeAsync timeout on attempt {Attempt} idempotency_key={Key}",
                        attempt, request.IdempotencyKey);
                    lastException = ex;
                }
                catch (HttpRequestException ex)
                {
                    // greybeard: external -- network-level failure; safe to retry (idempotency key protects mutation)
                    _logger.LogWarning(
                        ex,
                        "ChargeAsync network error on attempt {Attempt} idempotency_key={Key}",
                        attempt, request.IdempotencyKey);
                    lastException = ex;
                }
                finally
                {
                    response?.Dispose();
                }

                // Don't sleep after the last attempt
                if (attempt < MaxAttempts)
                {
                    // greybeard: external -- jittered exponential backoff; avoids thundering herd
                    var backoffMs = Math.Min(
                        BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1),
                        MaxBackoff.TotalMilliseconds);
                    var jitteredMs = backoffMs * (0.5 + rng.NextDouble() * 0.5);
                    await Task.Delay(TimeSpan.FromMilliseconds(jitteredMs), cancellationToken);
                }
            }

            // greybeard: external -- all retries exhausted; surface as failed charge, not exception,
            //            so the caller can enqueue a compensating action / dead-letter the job
            _logger.LogError(
                lastException,
                "ChargeAsync exhausted {Max} attempts idempotency_key={Key}",
                MaxAttempts, request.IdempotencyKey);

            return new ChargeResult(
                Status:          ChargeStatus.Failed,
                GatewayChargeId: null,
                DeclineCode:     null,
                ErrorMessage:    "Payment gateway unreachable after retries. Idempotency key preserved; safe to retry later.");
        }
    }
}

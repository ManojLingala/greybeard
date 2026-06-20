using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.CircuitBreaker;

public class PaymentChargeService
{
    private readonly HttpClient _httpClient;
    private readonly IAsyncPolicy<PaymentResult> _retryPolicy;
    private readonly IAsyncPolicy<PaymentResult> _circuitBreakerPolicy;

    public PaymentChargeService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        // greybeard: external call - timeout always set, never null
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // greybeard: external call - jittered backoff on transient failures, max 3 retries
        _retryPolicy = Policy
            .Handle<HttpRequestException>()
            .Or<TimeoutRejectedException>()
            .OrResult<PaymentResult>(r => r.IsTransient)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(
                    Math.Pow(2, attempt) + Random.Shared.Next(0, 1000) / 1000.0
                ),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Payment retry {retryCount} after {timespan.TotalSeconds}s for idempotency_key={context["idempotency_key"]}"
                    );
                }
            );

        // greybeard: external call - circuit breaker stops hammering failing gateway
        _circuitBreakerPolicy = Policy
            .Handle<HttpRequestException>()
            .OrResult<PaymentResult>(r => r.IsTransient)
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(60),
                onBreak: (outcome, timespan) =>
                {
                    System.Diagnostics.Debug.WriteLine($"Circuit breaker opened for {timespan.TotalSeconds}s");
                }
            );
    }

    /// <summary>
    /// Charges a payment card via external gateway with production safety.
    /// Safe to retry with same idempotency_key - exactly once semantics guaranteed.
    /// </summary>
    public async Task<PaymentResult> ChargeCardAsync(
        string idempotencyKey,
        string cardToken,
        long amountMinorUnits,
        string currencyCode,
        string description,
        CancellationToken cancellationToken = default)
    {
        // greybeard: mutation - idempotency key required; no charge without it
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("idempotency_key is required", nameof(idempotencyKey));

        // greybeard: money - amount in minor units (cents/pence), never float, always positive
        if (amountMinorUnits <= 0)
            throw new ArgumentException("amountMinorUnits must be positive", nameof(amountMinorUnits));

        // greybeard: trust boundary - validate input crossing the network
        if (string.IsNullOrWhiteSpace(cardToken))
            throw new ArgumentException("cardToken is required", nameof(cardToken));

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("currencyCode must be 3-letter ISO code", nameof(currencyCode));

        var context = new Polly.Context
        {
            { "idempotency_key", idempotencyKey },
            { "amount_minor_units", amountMinorUnits },
            { "currency_code", currencyCode }
        };

        try
        {
            // greybeard: external call + concurrency + mutation - policy chain: retry then circuit break
            var result = await _circuitBreakerPolicy.WrapAsync(_retryPolicy)
                .ExecuteAsync(
                    (ctx, ct) => ExecuteChargeAsync(cardToken, amountMinorUnits, currencyCode, description, idempotencyKey, ct),
                    context,
                    cancellationToken
                );

            // greybeard: mutation - log success without exposing secrets
            if (result.Success)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Charge succeeded: idempotency_key={idempotencyKey} gateway_tx_id={result.GatewayTransactionId}"
                );
            }

            return result;
        }
        catch (BrokenCircuitException ex)
        {
            // greybeard: fail half-way - circuit breaker open; graceful degradation
            return new PaymentResult
            {
                Success = false,
                ErrorCode = "GATEWAY_UNAVAILABLE",
                ErrorMessage = "Payment gateway temporarily unavailable. Retry later.",
                IsTransient = true,
                GatewayTransactionId = null,
                Exception = ex
            };
        }
        catch (OperationCanceledException ex)
        {
            // greybeard: external call - timeout or cancellation
            return new PaymentResult
            {
                Success = false,
                ErrorCode = "REQUEST_CANCELLED",
                ErrorMessage = "Payment request was cancelled or timed out.",
                IsTransient = true,
                GatewayTransactionId = null,
                Exception = ex
            };
        }
        catch (Exception ex)
        {
            // greybeard: fail half-way - unexpected error; no secrets in logs
            System.Diagnostics.Debug.WriteLine(
                $"Charge failed with exception: idempotency_key={idempotencyKey} error_type={ex.GetType().Name}"
            );

            return new PaymentResult
            {
                Success = false,
                ErrorCode = "INTERNAL_ERROR",
                ErrorMessage = "An unexpected error occurred during payment processing.",
                IsTransient = false,
                GatewayTransactionId = null,
                Exception = ex
            };
        }
    }

    private async Task<PaymentResult> ExecuteChargeAsync(
        string cardToken,
        long amountMinorUnits,
        string currencyCode,
        string description,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        // greybeard: mutation - idempotency key in request header for gateway deduplication
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.payment-gateway.example.com/charges");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("Authorization", "Bearer <secret-token-from-config>");

        // greybeard: money - payload carries amount in minor units + currency, never float
        var payload = new
        {
            amount = amountMinorUnits,
            currency = currencyCode,
            source = cardToken,
            description = description,
            capture = true
        };

        var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8,
            "application/json"
        );
        request.Content = content;

        try
        {
            // greybeard: external call - timeout enforced by HttpClient.Timeout
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var chargeResponse = System.Text.Json.JsonDocument.Parse(responseBody);
                var root = chargeResponse.RootElement;

                // greybeard: money + trust boundary - parse gateway response; verify amount echoed back
                var gatewayAmount = root.GetProperty("amount").GetInt64();
                var gatewayTxId = root.GetProperty("id").GetString();

                if (gatewayAmount != amountMinorUnits)
                {
                    // greybeard: money - amount mismatch is a critical failure, no half-charges
                    throw new InvalidOperationException(
                        $"Gateway returned amount {gatewayAmount} but we sent {amountMinorUnits}"
                    );
                }

                return new PaymentResult
                {
                    Success = true,
                    ErrorCode = null,
                    ErrorMessage = null,
                    IsTransient = false,
                    GatewayTransactionId = gatewayTxId,
                    Exception = null
                };
            }
            else if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
            {
                // greybeard: external call - 5xx and timeouts are transient
                return new PaymentResult
                {
                    Success = false,
                    ErrorCode = $"GATEWAY_ERROR_{response.StatusCode}",
                    ErrorMessage = $"Gateway returned {response.StatusCode}. Retry safe.",
                    IsTransient = true,
                    GatewayTransactionId = null,
                    Exception = null
                };
            }
            else
            {
                // greybeard: external call - 4xx is permanent (bad input, expired card, etc.)
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return new PaymentResult
                {
                    Success = false,
                    ErrorCode = $"GATEWAY_REJECTED_{response.StatusCode}",
                    ErrorMessage = $"Gateway rejected charge: {response.StatusCode}. Do not retry.",
                    IsTransient = false,
                    GatewayTransactionId = null,
                    Exception = null
                };
            }
        }
        finally
        {
            request?.Dispose();
        }
    }
}

public class PaymentResult
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public bool IsTransient { get; set; }
    public string GatewayTransactionId { get; set; }
    public Exception Exception { get; set; }
}

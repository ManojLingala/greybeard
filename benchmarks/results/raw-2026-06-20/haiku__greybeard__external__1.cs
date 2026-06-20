using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.CircuitBreaker;

public class PaymentCharger
{
    private readonly HttpClient _httpClient;
    private readonly IAsyncPolicy<HttpResponseMessage> _resiliencePolicy;
    private readonly ILogger _logger;

    public PaymentCharger(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // greybeard: external call - timeout always. Jittered backoff. Circuit breaker on repeated failure.
        _resiliencePolicy = Policy
            .Handle<HttpRequestException>()
            .Or<OperationCanceledException>()
            .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt =>
                {
                    var baseDelay = TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100);
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 50));
                    return baseDelay + jitter;
                },
                onRetry: (outcome, delay, retryCount, context) =>
                {
                    _logger.LogWarning(
                        "Payment retry {RetryCount} after {DelayMs}ms. IdempotencyKey={IdempotencyKey}",
                        retryCount, delay.TotalMilliseconds, context["IdempotencyKey"]);
                })
            .WrapAsync(Policy
                .Handle<HttpRequestException>()
                .Or<OperationCanceledException>()
                .CircuitBreakerAsync<HttpResponseMessage>(
                    handledEventsAllowedBeforeBreaking: 5,
                    durationOfBreak: TimeSpan.FromSeconds(30),
                    onBreak: (outcome, duration) =>
                    {
                        _logger.LogError("Payment gateway circuit breaker opened for {DurationSeconds}s", duration.TotalSeconds);
                    }));
    }

    /// <summary>
    /// Charges a card via external payment gateway with full resilience and safety.
    /// </summary>
    /// <param name="amountMinorUnits">Amount in minor units (e.g., cents for USD). Must be >= 0.</param>
    /// <param name="currencyCode">ISO 4217 currency code (e.g., "USD", "EUR"). Must not be null or empty.</param>
    /// <param name="cardToken">PCI-compliant token from payment processor. Must not be null or empty.</param>
    /// <param name="idempotencyKey">Unique key for this charge attempt. Required for safe retries.</param>
    /// <param name="cancellationToken">Cancellation token with configured timeout.</param>
    /// <returns>Payment result with transaction ID on success, null on circuit breaker open.</returns>
    public async Task<PaymentResult> ChargeCardAsync(
        long amountMinorUnits,
        string currencyCode,
        string cardToken,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        // greybeard: money - integer minor-units, never float. Explicit validation. Currency code travels with the amount.
        if (amountMinorUnits < 0)
            throw new ArgumentException("Amount must be non-negative", nameof(amountMinorUnits));
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("Currency code must be a 3-letter ISO 4217 code", nameof(currencyCode));

        // greybeard: mutation - idempotency key. Safe to retry. Exactly-once effect.
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key is required for payment safety", nameof(idempotencyKey));

        // greybeard: trust-boundary validation - never trust input crossing a boundary.
        if (string.IsNullOrWhiteSpace(cardToken))
            throw new ArgumentException("Card token is required", nameof(cardToken));

        var context = new Polly.Context
        {
            { "IdempotencyKey", idempotencyKey },
            { "Amount", amountMinorUnits },
            { "Currency", currencyCode }
        };

        try
        {
            // greybeard: external call - timeout via CancellationToken. Never send secrets in logs.
            _logger.LogInformation(
                "Charging card. IdempotencyKey={IdempotencyKey}, Amount={Amount} {Currency}",
                idempotencyKey, amountMinorUnits, currencyCode);

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/charges")
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        amount_minor_units = amountMinorUnits,
                        currency_code = currencyCode.ToUpperInvariant(),
                        card_token = cardToken, // greybeard: trust-boundary - validated, not logged
                        idempotency_key = idempotencyKey
                    }),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };

            // greybeard: external call - timeout always set via cancellation token.
            var response = await _resiliencePolicy.ExecuteAsync(
                (ctx, ct) => _httpClient.SendAsync(request, ct),
                context,
                cancellationToken);

            // greybeard: can it fail half-way - check for partial success. Graceful degradation.
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Payment gateway error. IdempotencyKey={IdempotencyKey}, Status={StatusCode}",
                    idempotencyKey, response.StatusCode);

                // greybeard: partial failure is a first-class path. Caller decides compensating action.
                return new PaymentResult
                {
                    Success = false,
                    TransactionId = null,
                    ErrorCode = response.StatusCode.ToString(),
                    IsRetryable = IsRetryableStatusCode(response.StatusCode)
                };
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = System.Text.Json.JsonDocument.Parse(responseBody);
            var transactionId = result.RootElement.GetProperty("transaction_id").GetString();

            _logger.LogInformation(
                "Charge succeeded. IdempotencyKey={IdempotencyKey}, TransactionId={TransactionId}",
                idempotencyKey, transactionId);

            return new PaymentResult
            {
                Success = true,
                TransactionId = transactionId,
                ErrorCode = null,
                IsRetryable = false
            };
        }
        catch (OperationCanceledException)
        {
            // greybeard: external call timeout - log as distinct failure for observability.
            _logger.LogError(
                "Payment request timeout. IdempotencyKey={IdempotencyKey}",
                idempotencyKey);

            return new PaymentResult
            {
                Success = false,
                TransactionId = null,
                ErrorCode = "TIMEOUT",
                IsRetryable = true
            };
        }
        catch (BrokenCircuitException)
        {
            // greybeard: concurrency - circuit breaker open. Caller must backoff and retry later.
            _logger.LogError(
                "Payment gateway circuit breaker open. IdempotencyKey={IdempotencyKey}",
                idempotencyKey);

            return new PaymentResult
            {
                Success = false,
                TransactionId = null,
                ErrorCode = "CIRCUIT_OPEN",
                IsRetryable = false
            };
        }
        catch (Exception ex)
        {
            // greybeard: observable - unexpected errors logged with context but no secrets.
            _logger.LogError(ex,
                "Unexpected payment error. IdempotencyKey={IdempotencyKey}",
                idempotencyKey);

            return new PaymentResult
            {
                Success = false,
                TransactionId = null,
                ErrorCode = "INTERNAL_ERROR",
                IsRetryable = false
            };
        }
    }

    private static bool IsRetryableStatusCode(System.Net.HttpStatusCode statusCode)
    {
        return statusCode == System.Net.HttpStatusCode.RequestTimeout
            || statusCode == System.Net.HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;
    }
}

public class PaymentResult
{
    public bool Success { get; set; }
    public string TransactionId { get; set; }
    public string ErrorCode { get; set; }
    public bool IsRetryable { get; set; }
}

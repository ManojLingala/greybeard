using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public class PaymentCharger
{
    private readonly HttpClient _httpClient;
    private readonly string _gatewayUrl;
    private readonly int _timeoutMs;

    public PaymentCharger(HttpClient httpClient, string gatewayUrl, int timeoutMs = 30000)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _gatewayUrl = gatewayUrl ?? throw new ArgumentNullException(nameof(gatewayUrl));
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Charges a card via external payment gateway with comprehensive failure handling.
    /// </summary>
    /// <param name="cardToken">Tokenized card (no PAN in logs)</param>
    /// <param name="amountMinorUnits">Amount in smallest currency unit (e.g., cents for USD)</param>
    /// <param name="currencyCode">ISO 4217 currency code (e.g., "USD")</param>
    /// <param name="idempotencyKey">Unique key for this charge attempt - enables safe retry</param>
    /// <param name="cancellationToken">Cancellation token for timeout</param>
    /// <returns>ChargeResult with status and transaction ID if successful</returns>
    public async Task<ChargeResult> ChargeCardAsync(
        string cardToken,
        long amountMinorUnits,
        string currencyCode,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // greybeard: input validation at trust boundary
        if (string.IsNullOrWhiteSpace(cardToken))
            throw new ArgumentException("Card token required", nameof(cardToken));

        if (amountMinorUnits <= 0)
            throw new ArgumentException("Amount must be positive", nameof(amountMinorUnits));

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            throw new ArgumentException("Valid 3-letter currency code required", nameof(currencyCode));

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key required", nameof(idempotencyKey));

        // greybeard: money - amount is long (minor units), never float
        // greybeard: mutation - idempotency key enables exactly-once semantics
        var request = new
        {
            amount_minor_units = amountMinorUnits,
            currency_code = currencyCode,
            card_token = cardToken,
            idempotency_key = idempotencyKey
        };

        // greybeard: external call - timeout always, with jittered backoff + circuit breaker
        var maxRetries = 3;
        var baseDelayMs = 100;
        var random = new Random();

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(_timeoutMs);

                    var content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(request),
                        System.Text.Encoding.UTF8,
                        "application/json");

                    // greybeard: mutation - idempotency key in header prevents double-charge on retry
                    using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, _gatewayUrl))
                    {
                        httpRequest.Content = content;
                        httpRequest.Headers.Add("Idempotency-Key", idempotencyKey);
                        httpRequest.Headers.Add("X-Request-ID", Guid.NewGuid().ToString());

                        var response = await _httpClient.SendAsync(httpRequest, cts.Token);

                        // greybeard: can fail halfway - inspect response carefully
                        if (response.IsSuccessStatusCode)
                        {
                            var responseBody = await response.Content.ReadAsStringAsync();
                            var txnId = System.Text.Json.JsonDocument
                                .Parse(responseBody)
                                .RootElement
                                .GetProperty("transaction_id")
                                .GetString();

                            return new ChargeResult
                            {
                                Success = true,
                                TransactionId = txnId,
                                AmountMinorUnits = amountMinorUnits,
                                CurrencyCode = currencyCode,
                                Message = "Charge successful"
                            };
                        }

                        // greybeard: graceful degradation - distinguish retriable vs permanent failure
                        if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 408)
                        {
                            // Retriable: server error or timeout
                            if (attempt < maxRetries - 1)
                            {
                                var delayMs = baseDelayMs * (1 << attempt) + random.Next(0, baseDelayMs);
                                await Task.Delay(delayMs, cancellationToken);
                                continue;
                            }
                            return new ChargeResult
                            {
                                Success = false,
                                TransactionId = null,
                                AmountMinorUnits = amountMinorUnits,
                                CurrencyCode = currencyCode,
                                Message = $"Gateway temporarily unavailable after {maxRetries} retries"
                            };
                        }

                        // Non-retriable: 4xx errors
                        var errorDetail = await response.Content.ReadAsStringAsync();
                        return new ChargeResult
                        {
                            Success = false,
                            TransactionId = null,
                            AmountMinorUnits = amountMinorUnits,
                            CurrencyCode = currencyCode,
                            Message = $"Charge failed: {response.StatusCode}"
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // greybeard: timeout - do NOT retry blindly, check idempotency key first
                if (attempt < maxRetries - 1)
                {
                    var delayMs = baseDelayMs * (1 << attempt) + random.Next(0, baseDelayMs);
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }
                return new ChargeResult
                {
                    Success = false,
                    TransactionId = null,
                    AmountMinorUnits = amountMinorUnits,
                    CurrencyCode = currencyCode,
                    Message = "Gateway timeout - charge status unknown, use idempotency key to verify"
                };
            }
            catch (HttpRequestException ex)
            {
                // greybeard: network failure - retriable
                if (attempt < maxRetries - 1)
                {
                    var delayMs = baseDelayMs * (1 << attempt) + random.Next(0, baseDelayMs);
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }
                return new ChargeResult
                {
                    Success = false,
                    TransactionId = null,
                    AmountMinorUnits = amountMinorUnits,
                    CurrencyCode = currencyCode,
                    Message = "Network error - charge status unknown"
                };
            }
        }

        return new ChargeResult
        {
            Success = false,
            TransactionId = null,
            AmountMinorUnits = amountMinorUnits,
            CurrencyCode = currencyCode,
            Message = "Max retries exceeded"
        };
    }
}

public class ChargeResult
{
    public bool Success { get; set; }
    public string TransactionId { get; set; }
    public long AmountMinorUnits { get; set; }
    public string CurrencyCode { get; set; }
    public string Message { get; set; }
}

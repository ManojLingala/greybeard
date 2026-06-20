using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Charges a payment card with defensive practices for production payment processing.
/// Applies greybeard's ladder: money correctness, idempotency, external timeouts,
/// concurrency safety, and comprehensive failure logging.
/// </summary>
public class CardChargeService
{
    private readonly IPaymentProcessor _processor;
    private readonly ILogger<CardChargeService> _logger;
    private readonly IIdempotencyStore _idempotencyStore;

    // greybeard: Money correctness - all amounts in minor units (cents), never float
    private const int MinorUnitsPerDollar = 100;
    private const int MaxRetries = 3;
    private const int InitialBackoffMs = 100;

    public CardChargeService(
        IPaymentProcessor processor,
        ILogger<CardChargeService> logger,
        IIdempotencyStore idempotencyStore)
    {
        _processor = processor;
        _logger = logger;
        _idempotencyStore = idempotencyStore;
    }

    /// <summary>
    /// Charges a card with full defensive handling.
    /// </summary>
    /// <param name="idempotencyKey">Unique key for idempotent retries. Never changes for same charge.</param>
    /// <param name="amountCents">Amount in minor units (cents). Must be positive integer.</param>
    /// <param name="currencyCode">ISO 4217 currency code (e.g., "USD"). Travels with amount.</param>
    /// <param name="cardToken">Tokenized card from payment processor. Never logged.</param>
    /// <param name="metadata">Optional metadata for debugging (no sensitive data).</param>
    /// <returns>Charge result with processor transaction ID and status.</returns>
    public async Task<ChargeResult> ChargeCardAsync(
        string idempotencyKey,
        long amountCents,
        string currencyCode,
        string cardToken,
        string metadata = null)
    {
        // greybeard: Mutation - check idempotency first to avoid duplicate charges
        var cachedResult = await _idempotencyStore.GetAsync(idempotencyKey);
        if (cachedResult != null)
        {
            _logger.LogInformation(
                "Idempotent retry detected for charge {IdempotencyKey}. Returning cached result {TransactionId}.",
                idempotencyKey,
                cachedResult.TransactionId);
            return cachedResult;
        }

        // greybeard: Money - validate amount is positive integer in minor units
        if (amountCents <= 0)
        {
            var error = new InvalidOperationException($"Charge amount must be positive. Got: {amountCents} {currencyCode}");
            _logger.LogError(
                error,
                "Invalid charge amount for idempotency key {IdempotencyKey}: {AmountCents} {CurrencyCode}",
                idempotencyKey,
                amountCents,
                currencyCode);
            throw error;
        }

        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
        {
            var error = new ArgumentException($"Invalid currency code: {currencyCode}");
            _logger.LogError(
                error,
                "Invalid currency code for idempotency key {IdempotencyKey}",
                idempotencyKey);
            throw error;
        }

        var startTime = DateTime.UtcNow;
        ChargeResult result = null;
        Exception lastException = null;

        // greybeard: External call - retry with jittered backoff and explicit timeout
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Charging card attempt {Attempt}/{MaxRetries} for {IdempotencyKey}. Amount: {AmountCents} {CurrencyCode}. Metadata: {Metadata}",
                    attempt,
                    MaxRetries,
                    idempotencyKey,
                    amountCents,
                    currencyCode,
                    metadata ?? "none");

                // greybeard: External call - timeout enforced on processor call
                var cts = new System.Threading.CancellationTokenSource(timeoutMilliseconds: 10000);

                result = await _processor.ChargeAsync(
                    new PaymentRequest
                    {
                        IdempotencyKey = idempotencyKey,
                        AmountMinorUnits = amountCents,
                        CurrencyCode = currencyCode,
                        CardToken = cardToken
                    },
                    cts.Token);

                // greybeard: Mutation - persist idempotency result before returning
                // Ensures next retry of this idempotency key returns same result
                await _idempotencyStore.SaveAsync(idempotencyKey, result);

                _logger.LogInformation(
                    "Card charge successful for {IdempotencyKey}. TransactionId: {TransactionId}. Duration: {DurationMs}ms",
                    idempotencyKey,
                    result.TransactionId,
                    (DateTime.UtcNow - startTime).TotalMilliseconds);

                return result;
            }
            catch (OperationCanceledException ex)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Timeout on charge attempt {Attempt}/{MaxRetries} for {IdempotencyKey}. Elapsed: {DurationMs}ms",
                    attempt,
                    MaxRetries,
                    idempotencyKey,
                    (DateTime.UtcNow - startTime).TotalMilliseconds);
            }
            catch (TransientProcessorException ex)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Transient processor error on attempt {Attempt}/{MaxRetries} for {IdempotencyKey}. Error code: {ErrorCode}",
                    attempt,
                    MaxRetries,
                    idempotencyKey,
                    ex.ErrorCode);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogError(
                    ex,
                    "Unexpected error on charge attempt {Attempt}/{MaxRetries} for {IdempotencyKey}. Exception type: {ExceptionType}",
                    attempt,
                    MaxRetries,
                    idempotencyKey,
                    ex.GetType().Name);

                // greybeard: Half-way failure - don't retry on permanent errors
                if (ex is PermanentProcessorException)
                {
                    break;
                }
            }

            // greybeard: External call - jittered backoff to reduce thundering herd
            if (attempt < MaxRetries)
            {
                var backoffMs = InitialBackoffMs * (1 << (attempt - 1));
                var jitter = Random.Shared.Next(0, backoffMs / 2);
                var delayMs = backoffMs + jitter;

                _logger.LogInformation(
                    "Retry backoff for {IdempotencyKey}: {DelayMs}ms before attempt {NextAttempt}",
                    idempotencyKey,
                    delayMs,
                    attempt + 1);

                await Task.Delay(delayMs);
            }
        }

        // greybeard: Half-way failure - all retries exhausted, log final failure state
        _logger.LogError(
            lastException,
            "Card charge failed after {MaxRetries} attempts for {IdempotencyKey}. Total duration: {DurationMs}ms. Currency: {CurrencyCode}",
            MaxRetries,
            idempotencyKey,
            (DateTime.UtcNow - startTime).TotalMilliseconds,
            currencyCode);

        throw new ChargeFailedException(
            $"Failed to charge card after {MaxRetries} attempts. IdempotencyKey: {idempotencyKey}",
            lastException);
    }
}

// Supporting types
public class ChargeResult
{
    public string TransactionId { get; set; }
    public ChargeStatus Status { get; set; }
    public long AmountChargedMinorUnits { get; set; }
    public string CurrencyCode { get; set; }
    public DateTime ProcessedAt { get; set; }
}

public enum ChargeStatus
{
    Success = 1,
    Pending = 2,
    Failed = 3,
    Declined = 4
}

public class PaymentRequest
{
    public string IdempotencyKey { get; set; }
    public long AmountMinorUnits { get; set; }
    public string CurrencyCode { get; set; }
    public string CardToken { get; set; }
}

public interface IPaymentProcessor
{
    Task<ChargeResult> ChargeAsync(PaymentRequest request, System.Threading.CancellationToken cancellationToken);
}

public interface IIdempotencyStore
{
    Task<ChargeResult> GetAsync(string idempotencyKey);
    Task SaveAsync(string idempotencyKey, ChargeResult result);
}

public class ChargeFailedException : Exception
{
    public ChargeFailedException(string message, Exception innerException)
        : base(message, innerException) { }
}

public class TransientProcessorException : Exception
{
    public string ErrorCode { get; set; }
    public TransientProcessorException(string message, string errorCode, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

public class PermanentProcessorException : Exception
{
    public PermanentProcessorException(string message, Exception innerException)
        : base(message, innerException) { }
}

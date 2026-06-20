using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public class PaymentService
{
    private readonly ILogger<PaymentService> _logger;
    private readonly IPaymentGateway _paymentGateway;

    public PaymentService(ILogger<PaymentService> logger, IPaymentGateway paymentGateway)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _paymentGateway = paymentGateway ?? throw new ArgumentNullException(nameof(paymentGateway));
    }

    /// <summary>
    /// Charges a card for the specified amount and logs the attempt for debugging purposes.
    /// </summary>
    /// <param name="cardToken">Tokenized card identifier (never log raw card numbers)</param>
    /// <param name="amountCents">Amount to charge in cents</param>
    /// <param name="currency">ISO 4217 currency code (e.g., "USD")</param>
    /// <param name="idempotencyKey">Unique key to prevent duplicate charges</param>
    /// <returns>A ChargeResult indicating success or failure details</returns>
    public async Task<ChargeResult> ChargeCardAsync(
        string cardToken,
        long amountCents,
        string currency,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(cardToken))
            throw new ArgumentException("Card token must not be empty.", nameof(cardToken));
        if (amountCents <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountCents), "Amount must be positive.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency must not be empty.", nameof(currency));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key must not be empty.", nameof(idempotencyKey));

        // Log the attempt before calling the gateway so we have a record even if the process crashes mid-flight.
        _logger.LogInformation(
            "Charge attempt started. IdempotencyKey={IdempotencyKey} CardTokenPrefix={CardTokenPrefix} Amount={AmountCents} {Currency}",
            idempotencyKey,
            cardToken.Length >= 4 ? cardToken[^4..] : "****",  // last 4 chars of token only
            amountCents,
            currency);

        ChargeResult result;
        try
        {
            result = await _paymentGateway.ChargeAsync(cardToken, amountCents, currency, idempotencyKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Charge attempt threw an unhandled exception. IdempotencyKey={IdempotencyKey} Amount={AmountCents} {Currency}",
                idempotencyKey,
                amountCents,
                currency);

            return new ChargeResult
            {
                Success = false,
                IdempotencyKey = idempotencyKey,
                ErrorCode = "GATEWAY_EXCEPTION",
                ErrorMessage = ex.Message,
                AttemptedAt = DateTimeOffset.UtcNow
            };
        }

        if (result.Success)
        {
            _logger.LogInformation(
                "Charge succeeded. IdempotencyKey={IdempotencyKey} ChargeId={ChargeId} Amount={AmountCents} {Currency}",
                idempotencyKey,
                result.ChargeId,
                amountCents,
                currency);
        }
        else
        {
            _logger.LogWarning(
                "Charge failed. IdempotencyKey={IdempotencyKey} ErrorCode={ErrorCode} ErrorMessage={ErrorMessage} Amount={AmountCents} {Currency}",
                idempotencyKey,
                result.ErrorCode,
                result.ErrorMessage,
                amountCents,
                currency);
        }

        return result;
    }
}

/// <summary>
/// Represents the outcome of a card charge attempt.
/// </summary>
public class ChargeResult
{
    public bool Success { get; set; }
    public string? ChargeId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset AttemptedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Abstraction over a payment gateway (Stripe, Braintree, etc.).
/// </summary>
public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(string cardToken, long amountCents, string currency, string idempotencyKey);
}

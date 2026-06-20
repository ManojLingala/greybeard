using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public class PaymentService
{
    private readonly IPaymentGateway _paymentGateway;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(IPaymentGateway paymentGateway, ILogger<PaymentService> logger)
    {
        _paymentGateway = paymentGateway ?? throw new ArgumentNullException(nameof(paymentGateway));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Charges a card for the specified amount and logs the attempt for debugging purposes.
    /// </summary>
    /// <param name="cardToken">Tokenized card identifier (never log raw card numbers)</param>
    /// <param name="amountInCents">Amount to charge in the smallest currency unit (e.g., cents)</param>
    /// <param name="currency">ISO 4217 currency code (e.g., "USD")</param>
    /// <param name="idempotencyKey">Unique key to prevent duplicate charges</param>
    /// <returns>A ChargeResult indicating success or failure with details</returns>
    public async Task<ChargeResult> ChargeCardAsync(
        string cardToken,
        long amountInCents,
        string currency,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(cardToken))
            throw new ArgumentException("Card token must not be empty.", nameof(cardToken));
        if (amountInCents <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountInCents), "Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency must not be empty.", nameof(currency));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key must not be empty.", nameof(idempotencyKey));

        _logger.LogInformation(
            "Attempting card charge. IdempotencyKey={IdempotencyKey}, AmountInCents={AmountInCents}, Currency={Currency}, CardTokenSuffix={CardTokenSuffix}",
            idempotencyKey,
            amountInCents,
            currency,
            GetTokenSuffix(cardToken));

        ChargeResult result;
        try
        {
            result = await _paymentGateway.ChargeAsync(cardToken, amountInCents, currency, idempotencyKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unhandled exception during card charge. IdempotencyKey={IdempotencyKey}, AmountInCents={AmountInCents}, Currency={Currency}, CardTokenSuffix={CardTokenSuffix}",
                idempotencyKey,
                amountInCents,
                currency,
                GetTokenSuffix(cardToken));

            return new ChargeResult
            {
                Success = false,
                ErrorCode = "EXCEPTION",
                ErrorMessage = ex.Message,
                IdempotencyKey = idempotencyKey
            };
        }

        if (result.Success)
        {
            _logger.LogInformation(
                "Card charge succeeded. IdempotencyKey={IdempotencyKey}, TransactionId={TransactionId}, AmountInCents={AmountInCents}, Currency={Currency}",
                idempotencyKey,
                result.TransactionId,
                amountInCents,
                currency);
        }
        else
        {
            _logger.LogWarning(
                "Card charge failed. IdempotencyKey={IdempotencyKey}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}, AmountInCents={AmountInCents}, Currency={Currency}, CardTokenSuffix={CardTokenSuffix}",
                idempotencyKey,
                result.ErrorCode,
                result.ErrorMessage,
                amountInCents,
                currency,
                GetTokenSuffix(cardToken));
        }

        return result;
    }

    // Returns only the last 4 characters of the token to avoid logging sensitive data.
    private static string GetTokenSuffix(string token) =>
        token.Length >= 4 ? "****" + token[^4..] : "****";
}

public class ChargeResult
{
    public bool Success { get; init; }
    public string? TransactionId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
}

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(string cardToken, long amountInCents, string currency, string idempotencyKey);
}

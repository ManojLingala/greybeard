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
    /// <param name="amountInCents">Amount to charge in cents</param>
    /// <param name="currency">ISO 4217 currency code (e.g., "USD")</param>
    /// <param name="idempotencyKey">Unique key to prevent duplicate charges</param>
    /// <returns>A ChargeResult indicating success or failure</returns>
    public async Task<ChargeResult> ChargeCardAsync(
        string cardToken,
        long amountInCents,
        string currency,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(cardToken))
            throw new ArgumentException("Card token must not be empty.", nameof(cardToken));
        if (amountInCents <= 0)
            throw new ArgumentException("Amount must be positive.", nameof(amountInCents));
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency must not be empty.", nameof(currency));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key must not be empty.", nameof(idempotencyKey));

        // Mask token for logging — show only last 4 chars
        var maskedToken = cardToken.Length > 4
            ? $"****{cardToken[^4..]}"
            : "****";

        _logger.LogInformation(
            "Initiating card charge. MaskedToken={MaskedToken}, AmountCents={AmountCents}, " +
            "Currency={Currency}, IdempotencyKey={IdempotencyKey}",
            maskedToken, amountInCents, currency, idempotencyKey);

        ChargeResult result;
        try
        {
            result = await _paymentGateway.ChargeAsync(cardToken, amountInCents, currency, idempotencyKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected exception while charging card. MaskedToken={MaskedToken}, " +
                "AmountCents={AmountCents}, Currency={Currency}, IdempotencyKey={IdempotencyKey}",
                maskedToken, amountInCents, currency, idempotencyKey);

            return new ChargeResult
            {
                Success = false,
                ErrorCode = "GATEWAY_EXCEPTION",
                ErrorMessage = ex.Message,
                IdempotencyKey = idempotencyKey
            };
        }

        if (result.Success)
        {
            _logger.LogInformation(
                "Card charge succeeded. MaskedToken={MaskedToken}, AmountCents={AmountCents}, " +
                "Currency={Currency}, TransactionId={TransactionId}, IdempotencyKey={IdempotencyKey}",
                maskedToken, amountInCents, currency, result.TransactionId, idempotencyKey);
        }
        else
        {
            _logger.LogWarning(
                "Card charge failed. MaskedToken={MaskedToken}, AmountCents={AmountCents}, " +
                "Currency={Currency}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}, " +
                "IdempotencyKey={IdempotencyKey}",
                maskedToken, amountInCents, currency, result.ErrorCode, result.ErrorMessage, idempotencyKey);
        }

        return result;
    }
}

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(string cardToken, long amountInCents, string currency, string idempotencyKey);
}

public class ChargeResult
{
    public bool Success { get; set; }
    public string? TransactionId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? IdempotencyKey { get; set; }
}

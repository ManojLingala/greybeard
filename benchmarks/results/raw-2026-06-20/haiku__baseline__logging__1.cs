using System;
using System.Collections.Generic;

public class CardChargeService
{
    private readonly ILogger _logger;

    public CardChargeService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Charges a card and logs the attempt for debugging purposes.
    /// </summary>
    /// <param name="cardNumber">The card number to charge</param>
    /// <param name="amount">The amount to charge</param>
    /// <param name="currency">The currency code (e.g., USD)</param>
    /// <returns>True if charge was successful, false otherwise</returns>
    public bool ChargeCard(string cardNumber, decimal amount, string currency = "USD")
    {
        if (string.IsNullOrEmpty(cardNumber))
        {
            _logger.LogError("Charge attempt failed: Card number is empty");
            return false;
        }

        if (amount <= 0)
        {
            _logger.LogError($"Charge attempt failed: Invalid amount {amount} for card {MaskCardNumber(cardNumber)}");
            return false;
        }

        try
        {
            _logger.LogInformation($"Attempting to charge card {MaskCardNumber(cardNumber)} for {amount} {currency}");

            // Simulate card processing
            var result = ProcessCardCharge(cardNumber, amount, currency);

            if (result.Success)
            {
                _logger.LogInformation($"Successfully charged card {MaskCardNumber(cardNumber)} for {amount} {currency}. Transaction ID: {result.TransactionId}");
                return true;
            }
            else
            {
                _logger.LogWarning($"Card charge failed for {MaskCardNumber(cardNumber)}: {result.ErrorMessage}. Reason: {result.ErrorCode}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Exception during card charge for {MaskCardNumber(cardNumber)}: {ex.Message}. Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    private string MaskCardNumber(string cardNumber)
    {
        if (cardNumber.Length < 4)
            return "****";

        return "**** **** **** " + cardNumber.Substring(cardNumber.Length - 4);
    }

    private ChargeResult ProcessCardCharge(string cardNumber, decimal amount, string currency)
    {
        // Placeholder for actual card processing logic
        // This would typically call a payment gateway API
        return new ChargeResult
        {
            Success = true,
            TransactionId = Guid.NewGuid().ToString(),
            ErrorCode = null,
            ErrorMessage = null
        };
    }
}

public interface ILogger
{
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message);
}

public class ChargeResult
{
    public bool Success { get; set; }
    public string TransactionId { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
}

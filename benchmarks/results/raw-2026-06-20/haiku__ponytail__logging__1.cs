using System;
using System.Collections.Generic;

public class CardChargeService
{
    private readonly ILogger _logger;

    public CardChargeService(ILogger logger)
    {
        _logger = logger;
    }

    public bool ChargeCard(string cardNumber, decimal amount)
    {
        try
        {
            _logger.LogInformation($"Attempting to charge card {MaskCardNumber(cardNumber)} for ${amount}");

            // Simulate card charging
            ValidateCard(cardNumber);
            ProcessCharge(cardNumber, amount);

            _logger.LogInformation($"Successfully charged card {MaskCardNumber(cardNumber)} for ${amount}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to charge card {MaskCardNumber(cardNumber)} for ${amount}. Error: {ex.Message}", ex);
            return false;
        }
    }

    private void ValidateCard(string cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber) || cardNumber.Length < 13)
            throw new ArgumentException("Invalid card number");
    }

    private void ProcessCharge(string cardNumber, decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive");
        // Actual payment processing logic would go here
    }

    private string MaskCardNumber(string cardNumber)
    {
        if (cardNumber.Length <= 4)
            return "****";
        return $"****{cardNumber.Substring(cardNumber.Length - 4)}";
    }
}

public interface ILogger
{
    void LogInformation(string message);
    void LogError(string message, Exception ex);
}

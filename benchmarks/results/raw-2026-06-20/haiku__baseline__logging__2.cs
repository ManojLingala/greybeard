using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

public class PaymentProcessor
{
    private readonly ILogger<PaymentProcessor> _logger;

    public PaymentProcessor(ILogger<PaymentProcessor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Charges a card and logs the attempt for debugging purposes.
    /// </summary>
    /// <param name="cardNumber">The card number to charge</param>
    /// <param name="amount">The amount to charge</param>
    /// <returns>True if the charge was successful, false otherwise</returns>
    public bool ChargeCard(string cardNumber, decimal amount)
    {
        var stopwatch = Stopwatch.StartNew();
        var maskedCardNumber = MaskCardNumber(cardNumber);

        try
        {
            _logger.LogInformation(
                "Attempting to charge card {MaskedCardNumber} for amount ${Amount}",
                maskedCardNumber,
                amount);

            // Simulate card processing
            if (string.IsNullOrEmpty(cardNumber) || amount <= 0)
            {
                throw new ArgumentException("Invalid card number or amount");
            }

            // Call payment gateway (simulated)
            bool chargeSuccessful = ProcessPayment(cardNumber, amount);

            if (chargeSuccessful)
            {
                stopwatch.Stop();
                _logger.LogInformation(
                    "Card charge successful for {MaskedCardNumber}, Amount: ${Amount}, Duration: {ElapsedMilliseconds}ms",
                    maskedCardNumber,
                    amount,
                    stopwatch.ElapsedMilliseconds);

                return true;
            }
            else
            {
                stopwatch.Stop();
                _logger.LogWarning(
                    "Card charge declined for {MaskedCardNumber}, Amount: ${Amount}, Duration: {ElapsedMilliseconds}ms",
                    maskedCardNumber,
                    amount,
                    stopwatch.ElapsedMilliseconds);

                return false;
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "Card charge failed with exception for {MaskedCardNumber}, Amount: ${Amount}, Duration: {ElapsedMilliseconds}ms, ErrorMessage: {ErrorMessage}",
                maskedCardNumber,
                amount,
                stopwatch.ElapsedMilliseconds,
                ex.Message);

            return false;
        }
    }

    private bool ProcessPayment(string cardNumber, decimal amount)
    {
        // Simulate payment processing
        // In production, this would call an actual payment gateway API
        return cardNumber.Length >= 13 && amount > 0;
    }

    private string MaskCardNumber(string cardNumber)
    {
        if (string.IsNullOrEmpty(cardNumber) || cardNumber.Length < 4)
            return "****";

        return cardNumber.Substring(cardNumber.Length - 4).PadLeft(cardNumber.Length, '*');
    }
}

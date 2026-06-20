using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public class CardChargeService
{
    private readonly ILogger<CardChargeService> _logger;

    public CardChargeService(ILogger<CardChargeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Charges a card and logs the attempt with detailed debugging information.
    /// </summary>
    /// <param name="cardNumber">The card number to charge</param>
    /// <param name="amount">The amount to charge in cents</param>
    /// <returns>True if charge was successful, false otherwise</returns>
    public async Task<bool> ChargeCardAsync(string cardNumber, decimal amount)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = Guid.NewGuid().ToString();

        try
        {
            _logger.LogInformation(
                "Card charge attempt started. CorrelationId: {CorrelationId}, Amount: {Amount}, CardLastFour: {CardLastFour}",
                correlationId,
                amount,
                cardNumber.Substring(Math.Max(0, cardNumber.Length - 4)));

            // Simulate card processing
            await Task.Delay(100);

            // Simulate validation
            if (string.IsNullOrEmpty(cardNumber) || amount <= 0)
            {
                _logger.LogWarning(
                    "Card charge validation failed. CorrelationId: {CorrelationId}, CardEmpty: {CardEmpty}, AmountValid: {AmountValid}",
                    correlationId,
                    string.IsNullOrEmpty(cardNumber),
                    amount > 0);
                return false;
            }

            stopwatch.Stop();
            _logger.LogInformation(
                "Card charge completed successfully. CorrelationId: {CorrelationId}, Amount: {Amount}, DurationMs: {DurationMs}",
                correlationId,
                amount,
                stopwatch.ElapsedMilliseconds);

            return true;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "Card charge attempt failed with exception. CorrelationId: {CorrelationId}, Amount: {Amount}, DurationMs: {DurationMs}, ErrorMessage: {ErrorMessage}",
                correlationId,
                amount,
                stopwatch.ElapsedMilliseconds,
                ex.Message);

            return false;
        }
    }
}

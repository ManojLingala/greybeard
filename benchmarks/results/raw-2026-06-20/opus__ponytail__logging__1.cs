using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public sealed class CardCharger
{
    private readonly IPaymentGateway _gateway;
    private readonly ILogger<CardCharger> _logger;

    public CardCharger(IPaymentGateway gateway, ILogger<CardCharger> logger)
    {
        _gateway = gateway;
        _logger = logger;
    }

    /// <summary>
    /// Charges the card and logs the attempt so failures can be debugged later.
    /// </summary>
    public async Task<ChargeResult> ChargeAsync(string chargeId, decimal amount, string currency)
    {
        // Log the attempt up front: if the call throws or crashes, we still have a record.
        // Note: we deliberately do NOT log card numbers/PAN/CVV — only non-sensitive identifiers.
        _logger.LogInformation(
            "Charge attempt {ChargeId}: {Amount} {Currency}",
            chargeId, amount, currency);

        try
        {
            var result = await _gateway.ChargeAsync(chargeId, amount, currency);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Charge {ChargeId} succeeded (transaction {TransactionId})",
                    chargeId, result.TransactionId);
            }
            else
            {
                // A declined charge isn't an exception, but we still want it in the logs.
                _logger.LogWarning(
                    "Charge {ChargeId} declined: {Reason}",
                    chargeId, result.FailureReason);
            }

            return result;
        }
        catch (Exception ex)
        {
            // Include the exception so the stack trace is captured for later debugging.
            _logger.LogError(ex,
                "Charge {ChargeId} failed for {Amount} {Currency}",
                chargeId, amount, currency);
            throw;
        }
    }
}

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(string chargeId, decimal amount, string currency);
}

public sealed record ChargeResult(
    bool Success,
    string? TransactionId = null,
    string? FailureReason = null);

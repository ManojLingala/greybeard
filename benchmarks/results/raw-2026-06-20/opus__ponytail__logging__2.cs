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

    // Charges the card and logs the attempt so failures can be debugged later.
    // Note: we log only non-sensitive identifiers (never the full PAN/CVV).
    public async Task<ChargeResult> ChargeAsync(ChargeRequest request)
    {
        using var scope = _logger.BeginScope(new
        {
            request.OrderId,
            CardLast4 = request.CardLast4,
            request.AmountMinorUnits,
            request.Currency
        });

        _logger.LogInformation("Charge attempt started.");

        try
        {
            var result = await _gateway.ChargeAsync(request);

            if (result.Succeeded)
            {
                _logger.LogInformation(
                    "Charge succeeded. TransactionId={TransactionId}",
                    result.TransactionId);
            }
            else
            {
                // Declines are expected outcomes, not exceptions: log as a warning
                // with the gateway's decline code so we can debug failures later.
                _logger.LogWarning(
                    "Charge declined. DeclineCode={DeclineCode} Reason={Reason}",
                    result.DeclineCode,
                    result.FailureReason);
            }

            return result;
        }
        catch (Exception ex)
        {
            // Unexpected failure (network, gateway error, etc.). Log the full
            // exception so the attempt can be debugged later, then rethrow.
            _logger.LogError(ex, "Charge attempt failed with an unexpected error.");
            throw;
        }
    }
}

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(ChargeRequest request);
}

public sealed record ChargeRequest(
    string OrderId,
    string CardLast4,
    long AmountMinorUnits,
    string Currency);

public sealed record ChargeResult(
    bool Succeeded,
    string? TransactionId,
    string? DeclineCode,
    string? FailureReason);

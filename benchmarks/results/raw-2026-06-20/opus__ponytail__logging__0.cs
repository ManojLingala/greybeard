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
    /// Charges a card and logs the attempt so failures can be debugged later.
    /// Never logs the raw card number — only the last four digits and the
    /// gateway's reference, which is what you actually need to debug.
    /// </summary>
    public async Task<ChargeResult> ChargeAsync(ChargeRequest request)
    {
        // Stable id to correlate the log lines for a single attempt.
        var attemptId = Guid.NewGuid();

        _logger.LogInformation(
            "Charge attempt {AttemptId}: {Amount} {Currency} on card ending {Last4}",
            attemptId, request.AmountMinorUnits, request.Currency, request.CardLast4);

        try
        {
            var result = await _gateway.ChargeAsync(request);

            if (result.Succeeded)
            {
                _logger.LogInformation(
                    "Charge attempt {AttemptId} succeeded, gateway ref {GatewayRef}",
                    attemptId, result.GatewayReference);
            }
            else
            {
                // Declines are an expected outcome, not an exception — log as a
                // warning with the gateway's reason so they can be debugged.
                _logger.LogWarning(
                    "Charge attempt {AttemptId} declined: {DeclineCode} {DeclineMessage} (gateway ref {GatewayRef})",
                    attemptId, result.DeclineCode, result.DeclineMessage, result.GatewayReference);
            }

            return result;
        }
        catch (Exception ex)
        {
            // Unexpected failure (timeout, gateway down, bug). Log with the
            // exception and attempt id so the failure can be traced later,
            // then rethrow — swallowing it would hide the problem.
            _logger.LogError(ex,
                "Charge attempt {AttemptId} failed for card ending {Last4}",
                attemptId, request.CardLast4);
            throw;
        }
    }
}

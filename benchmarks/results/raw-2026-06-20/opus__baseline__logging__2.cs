using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public class PaymentService
{
    private readonly IPaymentGateway _gateway;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(IPaymentGateway gateway, ILogger<PaymentService> logger)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Charges the given card and logs the attempt so failures can be debugged later.
    /// Returns the charge result; never throws for a declined card.
    /// </summary>
    public async Task<ChargeResult> ChargeCardAsync(ChargeRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        // Correlate every log line for this attempt so a single charge can be
        // traced end-to-end across the logs.
        var attemptId = Guid.NewGuid().ToString("N");

        // NOTE: We deliberately log only non-sensitive identifiers. The full PAN,
        // CVV, and expiry are NEVER logged. We log a masked last-four only.
        var maskedCard = MaskCardNumber(request.CardNumber);

        _logger.LogInformation(
            "Charge attempt started. AttemptId={AttemptId} OrderId={OrderId} " +
            "Amount={Amount} Currency={Currency} Card={MaskedCard}",
            attemptId, request.OrderId, request.AmountMinorUnits, request.Currency, maskedCard);

        try
        {
            var result = await _gateway.ChargeAsync(request).ConfigureAwait(false);

            if (result.Succeeded)
            {
                _logger.LogInformation(
                    "Charge succeeded. AttemptId={AttemptId} OrderId={OrderId} " +
                    "TransactionId={TransactionId} Amount={Amount} {Currency}",
                    attemptId, request.OrderId, result.TransactionId,
                    request.AmountMinorUnits, request.Currency);
            }
            else
            {
                // A decline is an expected outcome, not an exception. Log it at
                // Warning with the gateway's decline code/reason so it can be
                // debugged later without re-running the charge.
                _logger.LogWarning(
                    "Charge declined. AttemptId={AttemptId} OrderId={OrderId} " +
                    "DeclineCode={DeclineCode} Reason={Reason} Amount={Amount} {Currency}",
                    attemptId, request.OrderId, result.DeclineCode, result.DeclineReason,
                    request.AmountMinorUnits, request.Currency);
            }

            return result;
        }
        catch (Exception ex)
        {
            // Unexpected failure (network, gateway outage, etc.). Log the full
            // exception with the correlation id so the attempt can be diagnosed.
            _logger.LogError(ex,
                "Charge failed unexpectedly. AttemptId={AttemptId} OrderId={OrderId} " +
                "Amount={Amount} {Currency}",
                attemptId, request.OrderId, request.AmountMinorUnits, request.Currency);

            throw;
        }
    }

    private static string MaskCardNumber(string cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber) || cardNumber.Length < 4)
            return "****";

        return "**** **** **** " + cardNumber[^4..];
    }
}

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(ChargeRequest request);
}

public sealed class ChargeRequest
{
    public string OrderId { get; init; } = string.Empty;
    public string CardNumber { get; init; } = string.Empty;
    public long AmountMinorUnits { get; init; }
    public string Currency { get; init; } = "USD";
}

public sealed class ChargeResult
{
    public bool Succeeded { get; init; }
    public string? TransactionId { get; init; }
    public string? DeclineCode { get; init; }
    public string? DeclineReason { get; init; }
}

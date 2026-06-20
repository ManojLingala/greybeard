using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public class PaymentService
{
    private readonly ILogger<PaymentService> _logger;
    private readonly IPaymentGateway _gateway;

    public PaymentService(ILogger<PaymentService> logger, IPaymentGateway gateway)
    {
        _logger = logger;
        _gateway = gateway;
    }

    public async Task<ChargeResult> ChargeCardAsync(string cardToken, decimal amount, string currency = "USD")
    {
        _logger.LogInformation("Attempting charge: token={CardToken}, amount={Amount} {Currency}",
            cardToken, amount, currency);

        try
        {
            var result = await _gateway.ChargeAsync(cardToken, amount, currency);

            if (result.Success)
            {
                _logger.LogInformation("Charge succeeded: token={CardToken}, transactionId={TransactionId}",
                    cardToken, result.TransactionId);
            }
            else
            {
                _logger.LogWarning("Charge declined: token={CardToken}, reason={DeclineReason}",
                    cardToken, result.DeclineReason);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Charge failed with exception: token={CardToken}, amount={Amount} {Currency}",
                cardToken, amount, currency);
            throw;
        }
    }
}

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(string cardToken, decimal amount, string currency);
}

public class ChargeResult
{
    public bool Success { get; init; }
    public string? TransactionId { get; init; }
    public string? DeclineReason { get; init; }
}

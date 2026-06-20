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
        _logger.LogInformation("Attempting to charge card token={CardToken} amount={Amount} currency={Currency}",
            cardToken, amount, currency);

        try
        {
            var result = await _gateway.ChargeAsync(cardToken, amount, currency);

            if (result.Success)
            {
                _logger.LogInformation("Card charge succeeded token={CardToken} amount={Amount} transactionId={TransactionId}",
                    cardToken, amount, result.TransactionId);
            }
            else
            {
                _logger.LogWarning("Card charge declined token={CardToken} amount={Amount} reason={Reason}",
                    cardToken, amount, result.DeclineReason);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Card charge failed with exception token={CardToken} amount={Amount}",
                cardToken, amount);
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

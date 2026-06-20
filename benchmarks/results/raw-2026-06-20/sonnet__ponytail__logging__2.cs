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
        _logger.LogInformation(
            "Attempting card charge: token={CardToken}, amount={Amount} {Currency}",
            MaskToken(cardToken), amount, currency);

        try
        {
            var result = await _gateway.ChargeAsync(cardToken, amount, currency);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Card charge succeeded: token={CardToken}, amount={Amount} {Currency}, transactionId={TransactionId}",
                    MaskToken(cardToken), amount, currency, result.TransactionId);
            }
            else
            {
                _logger.LogWarning(
                    "Card charge declined: token={CardToken}, amount={Amount} {Currency}, reason={Reason}",
                    MaskToken(cardToken), amount, currency, result.ErrorMessage);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Card charge failed with exception: token={CardToken}, amount={Amount} {Currency}",
                MaskToken(cardToken), amount, currency);

            return new ChargeResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static string MaskToken(string token) =>
        token?.Length > 4 ? $"****{token[^4..]}" : "****";
}

public interface IPaymentGateway
{
    Task<ChargeResult> ChargeAsync(string cardToken, decimal amount, string currency);
}

public class ChargeResult
{
    public bool Success { get; set; }
    public string TransactionId { get; set; }
    public string ErrorMessage { get; set; }
}

using System;
using System.Threading.Tasks;

/// <summary>
/// Charges a card and logs the attempt for debugging purposes.
/// </summary>
public class CardCharger
{
    private readonly ILogger _logger;
    private readonly IPaymentProvider _paymentProvider;

    public CardCharger(ILogger logger, IPaymentProvider paymentProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _paymentProvider = paymentProvider ?? throw new ArgumentNullException(nameof(paymentProvider));
    }

    /// <summary>
    /// Charges a card with the specified amount and logs the result.
    /// </summary>
    /// <param name="cardToken">The payment card token.</param>
    /// <param name="amountInCents">The amount to charge in cents.</param>
    /// <returns>True if the charge succeeded; false otherwise.</returns>
    public async Task<bool> ChargeCardAsync(string cardToken, int amountInCents)
    {
        var attemptId = Guid.NewGuid().ToString();
        var startTime = DateTime.UtcNow;

        try
        {
            _logger.LogInformation(
                "Card charge attempt started. AttemptId={attemptId}, Amount={amountInCents}",
                attemptId,
                amountInCents);

            var result = await _paymentProvider.ChargeAsync(cardToken, amountInCents);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Card charge succeeded. AttemptId={attemptId}, TransactionId={transactionId}, Duration={duration}ms",
                    attemptId,
                    result.TransactionId,
                    (DateTime.UtcNow - startTime).TotalMilliseconds);
                return true;
            }
            else
            {
                _logger.LogWarning(
                    "Card charge failed. AttemptId={attemptId}, ErrorCode={errorCode}, ErrorMessage={errorMessage}, Duration={duration}ms",
                    attemptId,
                    result.ErrorCode,
                    result.ErrorMessage,
                    (DateTime.UtcNow - startTime).TotalMilliseconds);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Card charge threw an exception. AttemptId={attemptId}, Duration={duration}ms",
                attemptId,
                (DateTime.UtcNow - startTime).TotalMilliseconds);
            return false;
        }
    }
}

/// <summary>
/// Minimal interfaces for dependency injection.
/// </summary>
public interface ILogger
{
    void LogInformation(string message, params object[] args);
    void LogWarning(string message, params object[] args);
    void LogError(Exception ex, string message, params object[] args);
}

public interface IPaymentProvider
{
    Task<ChargeResult> ChargeAsync(string cardToken, int amountInCents);
}

public class ChargeResult
{
    public bool Success { get; set; }
    public string TransactionId { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
}

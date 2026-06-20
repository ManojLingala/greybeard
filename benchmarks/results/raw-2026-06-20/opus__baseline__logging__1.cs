using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Payments
{
    public sealed class CardCharger
    {
        private readonly IPaymentGateway _gateway;
        private readonly ILogger<CardCharger> _logger;

        public CardCharger(IPaymentGateway gateway, ILogger<CardCharger> logger)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Charges a card and logs the attempt so failures can be debugged later.
        /// </summary>
        /// <param name="request">The charge request. Card data is never logged in full.</param>
        /// <param name="cancellationToken">Token to cancel the in-flight charge.</param>
        /// <returns>The result of the charge attempt.</returns>
        public async Task<ChargeResult> ChargeAsync(
            ChargeRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // A correlation id lets us tie together every log line for this one attempt.
            var attemptId = Guid.NewGuid().ToString("N");

            // Log the attempt up front. We deliberately log only non-sensitive
            // metadata: never the full PAN, CVV, or expiry. The last four digits
            // are enough to identify the card when debugging.
            using (_logger.BeginScope(new Dictionary<string, object?>
                   {
                       ["AttemptId"] = attemptId,
                       ["CustomerId"] = request.CustomerId,
                       ["CardLast4"] = MaskToLast4(request.CardNumber),
                       ["AmountMinor"] = request.AmountMinorUnits,
                       ["Currency"] = request.Currency,
                   }))
            {
                _logger.LogInformation(
                    "Charge attempt started: {AmountMinor} {Currency} for customer {CustomerId}.",
                    request.AmountMinorUnits,
                    request.Currency,
                    request.CustomerId);

                try
                {
                    ChargeResult result = await _gateway
                        .ChargeAsync(request, cancellationToken)
                        .ConfigureAwait(false);

                    if (result.Succeeded)
                    {
                        _logger.LogInformation(
                            "Charge succeeded. TransactionId={TransactionId}.",
                            result.TransactionId);
                    }
                    else
                    {
                        // A declined charge is not an exception, but we still
                        // want the gateway's decline code/message recorded so the
                        // failure can be investigated later.
                        _logger.LogWarning(
                            "Charge declined. Code={DeclineCode}, GatewayMessage={GatewayMessage}.",
                            result.DeclineCode,
                            result.GatewayMessage);
                    }

                    return result;
                }
                catch (OperationCanceledException)
                {
                    // Honor cancellation without treating it as a payment failure.
                    _logger.LogInformation("Charge attempt cancelled.");
                    throw;
                }
                catch (Exception ex)
                {
                    // Log the full exception (with the correlation scope above) so
                    // the stack trace and context are available for debugging.
                    _logger.LogError(
                        ex,
                        "Charge attempt failed unexpectedly for customer {CustomerId}.",
                        request.CustomerId);
                    throw;
                }
            }
        }

        private static string MaskToLast4(string? cardNumber)
        {
            if (string.IsNullOrEmpty(cardNumber) || cardNumber.Length < 4)
            {
                return "****";
            }

            return string.Concat("****", cardNumber.Substring(cardNumber.Length - 4));
        }
    }

    public interface IPaymentGateway
    {
        Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken);
    }

    public sealed class ChargeRequest
    {
        public string CustomerId { get; init; } = string.Empty;
        public string CardNumber { get; init; } = string.Empty;
        public long AmountMinorUnits { get; init; }
        public string Currency { get; init; } = "USD";
    }

    public sealed class ChargeResult
    {
        public bool Succeeded { get; init; }
        public string? TransactionId { get; init; }
        public string? DeclineCode { get; init; }
        public string? GatewayMessage { get; init; }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Payments
{
    /// <summary>
    /// Result of a card charge attempt.
    /// </summary>
    public sealed record ChargeResult(bool Succeeded, string? TransactionId, string? FailureReason);

    /// <summary>
    /// Request describing a card charge.
    /// </summary>
    public sealed record ChargeRequest(
        string CardToken,
        long AmountMinorUnits,
        string Currency,
        string IdempotencyKey);

    public interface IPaymentGateway
    {
        Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken cancellationToken);
    }

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
        /// Charges a card and logs the attempt and its outcome so that failures can be
        /// investigated later. Sensitive data (full card numbers, CVV, raw tokens) is never
        /// logged; only a non-reversible reference and request metadata are recorded.
        /// </summary>
        /// <param name="request">The charge request.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The result of the charge attempt.</returns>
        public async Task<ChargeResult> ChargeAsync(
            ChargeRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            // A safe, correlatable reference for debugging. The idempotency key uniquely
            // identifies the attempt without exposing card data.
            var attemptRef = request.IdempotencyKey;

            // Scope groups all log lines for this attempt under shared correlation fields.
            using var scope = _logger.BeginScope(new
            {
                AttemptRef = attemptRef,
                Amount = request.AmountMinorUnits,
                Currency = request.Currency
            });

            _logger.LogInformation(
                "Charge attempt started: {Amount} {Currency} (ref {AttemptRef}).",
                request.AmountMinorUnits, request.Currency, attemptRef);

            try
            {
                ChargeResult result = await _gateway
                    .ChargeAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Succeeded)
                {
                    _logger.LogInformation(
                        "Charge succeeded: transaction {TransactionId} (ref {AttemptRef}).",
                        result.TransactionId, attemptRef);
                }
                else
                {
                    // Declines and other "expected" failures: log enough to debug,
                    // but at Warning since this is not an exceptional condition.
                    _logger.LogWarning(
                        "Charge declined: {FailureReason} (ref {AttemptRef}).",
                        result.FailureReason ?? "unknown", attemptRef);
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Charge canceled (ref {AttemptRef}).", attemptRef);
                throw;
            }
            catch (Exception ex)
            {
                // Unexpected failure (network, gateway outage, serialization, etc.).
                // Log the full exception so it can be debugged later, then surface it.
                _logger.LogError(
                    ex,
                    "Charge failed with an unexpected error (ref {AttemptRef}).",
                    attemptRef);
                throw;
            }
        }
    }
}

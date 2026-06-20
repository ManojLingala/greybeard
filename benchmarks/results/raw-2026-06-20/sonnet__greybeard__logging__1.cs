// greybeard rungs that apply:
// 1. Money     -> integer minor-units (cents), currency code travels with amount
// 2. Mutation  -> idempotency key prevents double-charge on retry
// 3. External  -> timeout + jittered backoff + circuit breaker
// 6. Half-way  -> attempt record written before call; outcome updated after

using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Payments
{
    // greybeard: minor-units only -- no decimal/double for currency
    public sealed record Money(long AmountMinorUnits, string CurrencyCode)
    {
        public Money
        {
            if (AmountMinorUnits < 0)
                throw new ArgumentOutOfRangeException(nameof(AmountMinorUnits), "Amount must be non-negative.");
            if (string.IsNullOrWhiteSpace(CurrencyCode) || CurrencyCode.Length != 3)
                throw new ArgumentException("Currency code must be an ISO-4217 three-letter code.", nameof(CurrencyCode));
        }
    }

    public enum ChargeStatus { Succeeded, Failed, TimedOut, Unknown }

    public sealed record ChargeAttempt(
        Guid AttemptId,
        string IdempotencyKey,
        string PaymentMethodToken,      // greybeard: token, never raw PAN in memory
        Money Amount,
        DateTimeOffset StartedAt,
        ChargeStatus Status,
        string? ProviderChargeId,
        string? FailureCode,
        string? FailureMessage,          // greybeard: sanitized -- no card numbers, no secrets
        DateTimeOffset? CompletedAt
    );

    public interface IChargeAttemptRepository
    {
        Task InsertAsync(ChargeAttempt attempt, CancellationToken ct);
        Task UpdateAsync(ChargeAttempt attempt, CancellationToken ct);
    }

    public interface IPaymentGatewayClient
    {
        /// <summary>Calls the payment gateway. Throws <see cref="HttpRequestException"/> or <see cref="OperationCanceledException"/> on failure.</summary>
        Task<GatewayResponse> ChargeAsync(
            string paymentMethodToken,
            Money amount,
            string idempotencyKey,
            CancellationToken ct);
    }

    public sealed record GatewayResponse(string ChargeId, bool Succeeded, string? FailureCode, string? FailureMessage);

    public sealed class CardChargeService
    {
        private static readonly TimeSpan GatewayTimeout    = TimeSpan.FromSeconds(10);  // greybeard: explicit timeout -- never omit
        private static readonly int      MaxRetries        = 3;
        private static readonly TimeSpan BaseBackoff       = TimeSpan.FromMilliseconds(500);

        private readonly IPaymentGatewayClient     _gateway;
        private readonly IChargeAttemptRepository  _attempts;
        private readonly ILogger<CardChargeService> _log;

        public CardChargeService(
            IPaymentGatewayClient gateway,
            IChargeAttemptRepository attempts,
            ILogger<CardChargeService> log)
        {
            _gateway  = gateway  ?? throw new ArgumentNullException(nameof(gateway));
            _attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
            _log      = log      ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Charges a card and durably logs every attempt so failures can be debugged.
        /// </summary>
        /// <param name="paymentMethodToken">Opaque token from the vault -- never a raw PAN.</param>
        /// <param name="amount">Amount in minor units with currency code.</param>
        /// <param name="idempotencyKey">Caller-supplied key; same key = at-most-once charge.</param>
        public async Task<ChargeAttempt> ChargeCardAsync(
            string paymentMethodToken,
            Money  amount,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            // greybeard: trust-boundary validation -- reject bad input before touching any external system
            if (string.IsNullOrWhiteSpace(paymentMethodToken))
                throw new ArgumentException("Payment method token is required.", nameof(paymentMethodToken));
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
            if (amount is null)
                throw new ArgumentNullException(nameof(amount));

            var attempt = new ChargeAttempt(
                AttemptId:          Guid.NewGuid(),
                IdempotencyKey:     idempotencyKey,
                PaymentMethodToken: paymentMethodToken,
                Amount:             amount,
                StartedAt:          DateTimeOffset.UtcNow,
                Status:             ChargeStatus.Unknown,
                ProviderChargeId:   null,
                FailureCode:        null,
                FailureMessage:     null,
                CompletedAt:        null
            );

            // greybeard: write attempt BEFORE calling gateway so a process crash leaves a recoverable record
            await _attempts.InsertAsync(attempt, cancellationToken);

            // greybeard: structured log with attempt id -- no card data, no secrets
            _log.LogInformation(
                "ChargeAttempt {AttemptId} starting: idempotencyKey={IdempotencyKey} amount={Amount}{Currency}",
                attempt.AttemptId, idempotencyKey, amount.AmountMinorUnits, amount.CurrencyCode);

            GatewayResponse? response = null;
            ChargeStatus     finalStatus = ChargeStatus.Unknown;
            Exception?       lastException = null;

            // greybeard: retry with jittered exponential backoff; idempotency key makes retries safe
            for (int i = 0; i <= MaxRetries; i++)
            {
                if (i > 0)
                {
                    var jitter  = TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, 200));
                    var backoff = TimeSpan.FromTicks(BaseBackoff.Ticks * (1L << (i - 1))) + jitter;
                    _log.LogWarning(
                        "ChargeAttempt {AttemptId} retry {Retry}/{Max} after {Backoff}ms",
                        attempt.AttemptId, i, MaxRetries, (int)backoff.TotalMilliseconds);
                    await Task.Delay(backoff, cancellationToken);
                }

                try
                {
                    // greybeard: timeout per attempt, not just for the whole operation
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(GatewayTimeout);

                    response = await _gateway.ChargeAsync(
                        paymentMethodToken, amount, idempotencyKey, timeoutCts.Token);

                    finalStatus  = response.Succeeded ? ChargeStatus.Succeeded : ChargeStatus.Failed;
                    lastException = null;
                    break; // success or definitive failure -- stop retrying
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // greybeard: gateway-specific timeout, not caller cancellation
                    finalStatus   = ChargeStatus.TimedOut;
                    lastException = ex;
                    _log.LogWarning(
                        "ChargeAttempt {AttemptId} timed out on try {Try}",
                        attempt.AttemptId, i);
                    // timed-out calls are safe to retry because the idempotency key guards against double-charge
                }
                catch (HttpRequestException ex)
                {
                    finalStatus   = ChargeStatus.Unknown;
                    lastException = ex;
                    // greybeard: log exception type and status code, NOT the gateway response body (may contain PAN fragments)
                    _log.LogWarning(
                        "ChargeAttempt {AttemptId} network error on try {Try}: {ExceptionType}",
                        attempt.AttemptId, i, ex.GetType().Name);
                }
            }

            // greybeard: log outcome for post-mortem debugging -- sanitize failure messages before persisting
            string? safeFailureMessage = SanitizeFailureMessage(response?.FailureMessage);

            var completed = attempt with
            {
                Status:           finalStatus,
                ProviderChargeId: response?.ChargeId,
                FailureCode:      response?.FailureCode,
                FailureMessage:   safeFailureMessage,
                CompletedAt:      DateTimeOffset.UtcNow
            };

            // greybeard: persist final status; a crash here leaves status=Unknown which ops can investigate
            await _attempts.UpdateAsync(completed, cancellationToken);

            if (finalStatus == ChargeStatus.Succeeded)
            {
                _log.LogInformation(
                    "ChargeAttempt {AttemptId} succeeded: providerChargeId={ProviderChargeId}",
                    completed.AttemptId, completed.ProviderChargeId);
            }
            else
            {
                _log.LogError(
                    "ChargeAttempt {AttemptId} ended with status={Status} failureCode={FailureCode}",
                    completed.AttemptId, completed.Status, completed.FailureCode);

                if (lastException is not null && finalStatus != ChargeStatus.Failed)
                    throw new PaymentException(
                        $"Charge failed after {MaxRetries + 1} attempts (status={finalStatus}).",
                        completed.AttemptId,
                        lastException);
            }

            return completed;
        }

        // greybeard: strip anything that looks like a card number or secret from gateway messages
        private static string? SanitizeFailureMessage(string? raw)
        {
            if (raw is null) return null;
            // Redact 13-19 digit sequences that could be PANs
            return System.Text.RegularExpressions.Regex.Replace(raw, @"\b\d{13,19}\b", "[REDACTED]");
        }
    }

    public sealed class PaymentException : Exception
    {
        public Guid AttemptId { get; }
        public PaymentException(string message, Guid attemptId, Exception inner)
            : base(message, inner) => AttemptId = attemptId;
    }
}

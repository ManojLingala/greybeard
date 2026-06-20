using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// Rungs that apply to this task:
// 1. Money     -> integer minor-units (cents), explicit currency code
// 2. Mutation  -> idempotency key prevents double-charge on retry
// 3. External  -> timeout + jittered backoff + circuit-breaker awareness
// 6. Partial failure -> charge attempt is logged before the call; outcome logged after

namespace Payments
{
    /// <summary>Represents an amount in minor units (e.g. cents) with its currency.</summary>
    public sealed record Money(long MinorUnits, string CurrencyCode)
    {
        public override string ToString() => $"{MinorUnits} {CurrencyCode}";
    }

    public sealed record ChargeRequest(
        string IdempotencyKey,   // greybeard: caller-supplied idempotency key -- safe to retry
        string PaymentMethodId,
        Money Amount,
        string Description);

    public sealed record ChargeResult(
        string IdempotencyKey,
        bool Success,
        string? ProviderChargeId,
        string? FailureCode,
        string? FailureMessage);

    public interface IPaymentProvider
    {
        /// <summary>
        /// Submits a charge to the payment provider.
        /// Implementations MUST honour the idempotency key.
        /// </summary>
        Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct);
    }

    public interface IChargeAttemptRepository
    {
        Task RecordAttemptStartAsync(string idempotencyKey, Money amount, string paymentMethodId, CancellationToken ct);
        Task RecordAttemptOutcomeAsync(ChargeResult result, TimeSpan elapsed, CancellationToken ct);
    }

    public sealed class CardChargeService
    {
        private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(15); // greybeard: hard timeout -- never wait forever for a bank
        private static readonly TimeSpan BaseBackoff    = TimeSpan.FromMilliseconds(200);
        private const int MaxRetries = 3;

        private readonly IPaymentProvider _provider;
        private readonly IChargeAttemptRepository _repo;
        private readonly ILogger<CardChargeService> _logger;
        private readonly Random _rng = new();

        public CardChargeService(
            IPaymentProvider provider,
            IChargeAttemptRepository repo,
            ILogger<CardChargeService> logger)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _repo     = repo     ?? throw new ArgumentNullException(nameof(repo));
            _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Charges a card and durably logs every attempt so failures can be debugged later.
        /// </summary>
        public async Task<ChargeResult> ChargeCardAsync(
            ChargeRequest request,
            CancellationToken cancellationToken = default)
        {
            // greybeard: trust-boundary validation -- never trust input crossing a boundary
            if (request is null)                     throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
                throw new ArgumentException("IdempotencyKey is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.PaymentMethodId))
                throw new ArgumentException("PaymentMethodId is required.", nameof(request));
            if (request.Amount.MinorUnits <= 0)
                throw new ArgumentOutOfRangeException(nameof(request), "Amount must be positive minor-units (cents).");
            if (string.IsNullOrWhiteSpace(request.Amount.CurrencyCode))
                throw new ArgumentException("CurrencyCode is required.", nameof(request));

            // greybeard: log attempt BEFORE the external call so a crash mid-flight is still visible
            await _repo.RecordAttemptStartAsync(
                request.IdempotencyKey, request.Amount, request.PaymentMethodId, cancellationToken);

            _logger.LogInformation(
                "Charge attempt starting. IdempotencyKey={IdempotencyKey} Amount={Amount} PaymentMethodId={PaymentMethodId}",
                request.IdempotencyKey,
                request.Amount,                    // greybeard: no secrets -- PaymentMethodId is a token, not a PAN; raw card data never logged
                request.PaymentMethodId);

            ChargeResult? result = null;
            Exception? lastException = null;
            var started = DateTimeOffset.UtcNow;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    // greybeard: timeout on every external call
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(ProviderTimeout);

                    result = await _provider.ChargeAsync(request, timeoutCts.Token);
                    lastException = null;
                    break; // success -- exit retry loop
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // greybeard: provider timed out -- log and retry; the idempotency key makes the retry safe
                    _logger.LogWarning(
                        "Charge timed out on attempt {Attempt}/{MaxRetries}. IdempotencyKey={IdempotencyKey}",
                        attempt, MaxRetries, request.IdempotencyKey);
                    lastException = new TimeoutException($"Payment provider did not respond within {ProviderTimeout.TotalSeconds}s.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Charge attempt {Attempt}/{MaxRetries} failed with exception. IdempotencyKey={IdempotencyKey}",
                        attempt, MaxRetries, request.IdempotencyKey);
                    lastException = ex;
                }

                if (attempt < MaxRetries)
                {
                    // greybeard: jittered exponential backoff -- avoids thundering herd against the payment provider
                    var jitter  = TimeSpan.FromMilliseconds(_rng.Next(0, 100));
                    var backoff = TimeSpan.FromTicks(BaseBackoff.Ticks * (1L << (attempt - 1))) + jitter;
                    _logger.LogDebug(
                        "Backing off {BackoffMs}ms before retry. IdempotencyKey={IdempotencyKey}",
                        backoff.TotalMilliseconds, request.IdempotencyKey);
                    await Task.Delay(backoff, cancellationToken);
                }
            }

            var elapsed = DateTimeOffset.UtcNow - started;

            if (result is null)
            {
                // greybeard: all retries exhausted -- build a failure result so the outcome is still persisted
                result = new ChargeResult(
                    IdempotencyKey:  request.IdempotencyKey,
                    Success:         false,
                    ProviderChargeId: null,
                    FailureCode:     "provider_unreachable",
                    FailureMessage:  lastException?.Message ?? "Unknown error after all retries.");
            }

            // greybeard: durable outcome log -- even on failure; this is what ops reads at 3am
            await _repo.RecordAttemptOutcomeAsync(result, elapsed, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Charge succeeded. IdempotencyKey={IdempotencyKey} ProviderChargeId={ProviderChargeId} ElapsedMs={ElapsedMs}",
                    result.IdempotencyKey, result.ProviderChargeId, elapsed.TotalMilliseconds);
            }
            else
            {
                // greybeard: no secrets in logs -- FailureMessage comes from the provider, not from raw card data
                _logger.LogError(
                    "Charge failed. IdempotencyKey={IdempotencyKey} FailureCode={FailureCode} FailureMessage={FailureMessage} ElapsedMs={ElapsedMs}",
                    result.IdempotencyKey, result.FailureCode, result.FailureMessage, elapsed.TotalMilliseconds);
            }

            return result;
        }
    }
}

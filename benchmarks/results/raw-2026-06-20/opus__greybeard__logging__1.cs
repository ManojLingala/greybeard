using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Payments;

// Rungs that apply to "charge a card and log the attempt":
//   1. Money       -> amount is integer minor units + currency code, never float/decimal-as-dollars.
//   2. Mutation    -> idempotency key so a retried charge does not double-bill.
//   3. External    -> timeout always, jittered backoff retry, circuit breaker on repeated failure.
//   7. Observable  -> structured log of every attempt WITHOUT secrets/PAN.
// Rungs 4/5/6 (concurrency, list reads, multi-step saga) don't apply to a single charge call -> skipped.

/// <summary>Amount carried as integer minor units with its currency. No floats touch money.</summary>
public readonly record struct Money(long MinorUnits, string Currency)
{
    // greybeard: rung 1 - money is integer minor units (e.g. cents); rounding happens upstream, explicitly, not here.
    public static Money Of(long minorUnits, string currency)
    {
        // greybeard: trust-boundary validation - reject nonsense before it reaches the processor.
        if (minorUnits <= 0) throw new ArgumentOutOfRangeException(nameof(minorUnits), "Charge amount must be positive minor units.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO-4217 code.", nameof(currency));
        return new Money(minorUnits, currency.ToUpperInvariant());
    }
}

public sealed record ChargeRequest(
    Money Amount,
    string CardToken,        // greybeard: a processor token/PAN-surrogate, NEVER a raw card number. Never logged.
    string IdempotencyKey);  // greybeard: rung 2 - caller-supplied stable key; same key => same charge, retried safely.

public enum ChargeOutcome { Approved, Declined, Retryable, PermanentError }

public sealed record ChargeResult(ChargeOutcome Outcome, string? ProcessorChargeId, string? DeclineCode);

/// <summary>Thin client over the upstream processor; assume the SDK enforces idempotency via header.</summary>
public interface ICardProcessor
{
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct);
}

public sealed class CardCharger
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan PerCallTimeout = TimeSpan.FromSeconds(8); // greybeard: rung 3 - external calls ALWAYS bound time.

    private readonly ICardProcessor _processor;
    private readonly ILogger<CardCharger> _logger;
    private readonly ICircuitBreaker _breaker; // greybeard: rung 3 - open after repeated failures so we stop hammering a sick bank.

    public CardCharger(ICardProcessor processor, ILogger<CardCharger> logger, ICircuitBreaker breaker)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
    }

    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // greybeard: rung 2 - without an idempotency key a retry could double-charge. Refuse to proceed.
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required to safely retry a charge.", nameof(request));

        // greybeard: rung 3 - fail fast (and cheaply) when the processor is known-unhealthy.
        if (!_breaker.AllowRequest())
        {
            _logger.LogWarning(
                "Charge short-circuited by open breaker. idem={IdempotencyKey} amount={MinorUnits} {Currency}",
                request.IdempotencyKey, request.Amount.MinorUnits, request.Amount.Currency);
            return new ChargeResult(ChargeOutcome.Retryable, null, "circuit_open");
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(PerCallTimeout); // greybeard: rung 3 - hard timeout per attempt; a hung bank never hangs us forever.

            try
            {
                var result = await _processor.ChargeAsync(request, attemptCts.Token).ConfigureAwait(false);

                // greybeard: rung 7 - log the attempt with safe fields only. NO card token, NO PAN, NO secrets.
                _logger.LogInformation(
                    "Charge attempt {Attempt}/{Max} outcome={Outcome} idem={IdempotencyKey} amount={MinorUnits} {Currency} chargeId={ChargeId} decline={DeclineCode}",
                    attempt, MaxAttempts, result.Outcome, request.IdempotencyKey,
                    request.Amount.MinorUnits, request.Amount.Currency,
                    result.ProcessorChargeId, result.DeclineCode);

                if (result.Outcome is ChargeOutcome.Approved or ChargeOutcome.Declined or ChargeOutcome.PermanentError)
                {
                    _breaker.RecordSuccess(); // greybeard: a clean processor reply (even a decline) means the dependency is healthy.
                    return result;
                }

                // Retryable -> fall through to backoff.
                _breaker.RecordFailure();
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or TimeoutException)
            {
                lastError = ex;
                _breaker.RecordFailure();
                // greybeard: log the failure CLASS, not the exception's raw payload, to avoid leaking processor secrets/PII.
                _logger.LogWarning(
                    "Charge attempt {Attempt}/{Max} transient failure ({Error}) idem={IdempotencyKey} amount={MinorUnits} {Currency}",
                    attempt, MaxAttempts, ex.GetType().Name, request.IdempotencyKey,
                    request.Amount.MinorUnits, request.Amount.Currency);

                if (ct.IsCancellationRequested) throw; // caller gave up -> respect it.
            }

            if (attempt < MaxAttempts)
            {
                // greybeard: rung 3 - jittered exponential backoff. The SAME idempotency key is reused on retry => exactly-once charge effect.
                var backoff = TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100 + Random.Shared.Next(0, 100));
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
        }

        _logger.LogError(
            "Charge exhausted {Max} attempts; surfacing retryable. idem={IdempotencyKey} amount={MinorUnits} {Currency}",
            MaxAttempts, request.IdempotencyKey, request.Amount.MinorUnits, request.Amount.Currency);

        // greybeard: ambiguous outcome - we may have charged on the last attempt. Report Retryable so the caller
        // re-drives with the SAME idempotency key rather than assuming failure and double-charging.
        return new ChargeResult(ChargeOutcome.Retryable, null, lastError?.GetType().Name ?? "unknown");
    }
}

public interface ICircuitBreaker
{
    bool AllowRequest();
    void RecordSuccess();
    void RecordFailure();
}

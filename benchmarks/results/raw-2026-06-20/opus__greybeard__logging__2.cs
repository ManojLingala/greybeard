using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Payments;

// greybeard: Money travels as integer minor-units + ISO currency code. No float, ever.
public readonly record struct Money(long MinorUnits, string Currency)
{
    // greybeard: validate at the trust boundary — reject nonsense before it reaches the processor.
    public void Validate()
    {
        if (MinorUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(MinorUnits), "Charge amount must be positive.");
        if (string.IsNullOrWhiteSpace(Currency) || Currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO code.", nameof(Currency));
    }
}

public sealed record ChargeRequest(
    string IdempotencyKey,   // greybeard: caller-supplied; the same key must never charge twice.
    string CardToken,        // greybeard: a vault token, NOT a PAN. Raw card data never enters this method.
    Money Amount);

public enum ChargeOutcome { Succeeded, Declined, Failed }

public sealed record ChargeResult(ChargeOutcome Outcome, string? ProcessorChargeId, string? DeclineReason);

public interface ICardProcessor
{
    // The concrete client is expected to honor the CancellationToken (timeout) we pass it.
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct);
}

// greybeard: a place to remember which idempotency keys we've already resolved — survives retries/restarts.
public interface IChargeLedger
{
    Task<ChargeResult?> TryGetAsync(string idempotencyKey, CancellationToken ct);
    Task SaveAsync(string idempotencyKey, ChargeResult result, CancellationToken ct);
}

// greybeard: trips after repeated processor failures so we stop hammering a dead dependency.
public interface ICircuitBreaker
{
    bool IsOpen { get; }
    void RecordSuccess();
    void RecordFailure();
}

public sealed class CardCharger
{
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(8);
    private const int MaxAttempts = 3;

    private readonly ICardProcessor _processor;
    private readonly IChargeLedger _ledger;
    private readonly ICircuitBreaker _breaker;
    private readonly ILogger<CardCharger> _logger;

    public CardCharger(
        ICardProcessor processor,
        IChargeLedger ledger,
        ICircuitBreaker breaker,
        ILogger<CardCharger> logger)
    {
        _processor = processor;
        _ledger = ledger;
        _breaker = breaker;
        _logger = logger;
    }

    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // greybeard: validate everything crossing the boundary before we touch money or the network.
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.CardToken))
            throw new ArgumentException("Card token is required.", nameof(request));
        request.Amount.Validate();

        // greybeard: never log the card token — log a stable, non-reversible scope key instead.
        using var scope = _logger.BeginScope(new
        {
            request.IdempotencyKey,
            request.Amount.MinorUnits,
            request.Amount.Currency
        });

        // greybeard: idempotency — a retried request returns the prior result instead of charging again.
        var existing = await _ledger.TryGetAsync(request.IdempotencyKey, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Charge {IdempotencyKey} already resolved as {Outcome}; replaying stored result.",
                request.IdempotencyKey, existing.Outcome);
            return existing;
        }

        // greybeard: circuit breaker — fail fast and degrade gracefully when the processor is down.
        if (_breaker.IsOpen)
        {
            _logger.LogWarning(
                "Circuit open; rejecting charge {IdempotencyKey} without calling processor.",
                request.IdempotencyKey);
            var degraded = new ChargeResult(ChargeOutcome.Failed, null, "processor_unavailable");
            // greybeard: do NOT persist transient infrastructure failures — the key stays retryable later.
            return degraded;
        }

        ChargeResult? lastResult = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // greybeard: half-way path — log the attempt BEFORE the call so an in-flight/hung charge is debuggable.
            _logger.LogInformation(
                "Charge attempt {Attempt}/{Max} for {IdempotencyKey} ({MinorUnits} {Currency}).",
                attempt, MaxAttempts, request.IdempotencyKey,
                request.Amount.MinorUnits, request.Amount.Currency);

            // greybeard: always a timeout. A network call with no deadline is an outage waiting to happen.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PerAttemptTimeout);

            try
            {
                var result = await _processor.ChargeAsync(request, timeoutCts.Token).ConfigureAwait(false);
                _breaker.RecordSuccess();
                lastResult = result;

                if (result.Outcome == ChargeOutcome.Succeeded)
                {
                    // greybeard: persist the terminal result keyed by idempotency BEFORE returning,
                    // so any future retry replays it instead of double-charging.
                    await _ledger.SaveAsync(request.IdempotencyKey, result, ct).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Charge {IdempotencyKey} succeeded; processorChargeId={ProcessorChargeId}.",
                        request.IdempotencyKey, result.ProcessorChargeId);
                    return result;
                }

                // greybeard: a decline is a deterministic, terminal answer — persist and stop, do not retry.
                if (result.Outcome == ChargeOutcome.Declined)
                {
                    await _ledger.SaveAsync(request.IdempotencyKey, result, ct).ConfigureAwait(false);
                    _logger.LogWarning(
                        "Charge {IdempotencyKey} declined; reason={DeclineReason}.",
                        request.IdempotencyKey, result.DeclineReason);
                    return result;
                }

                // Outcome == Failed: transient processor-side failure, fall through to retry/backoff.
                _logger.LogWarning(
                    "Charge {IdempotencyKey} attempt {Attempt} returned transient failure; reason={DeclineReason}.",
                    request.IdempotencyKey, attempt, result.DeclineReason);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller gave up — surface it, don't swallow.
                throw;
            }
            catch (OperationCanceledException)
            {
                // greybeard: our per-attempt timeout fired. The charge MAY have landed — treat as unknown, retryable.
                _breaker.RecordFailure();
                lastResult = new ChargeResult(ChargeOutcome.Failed, null, "timeout");
                _logger.LogError(
                    "Charge {IdempotencyKey} attempt {Attempt} timed out after {TimeoutSeconds}s.",
                    request.IdempotencyKey, attempt, PerAttemptTimeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                // greybeard: log the exception type/message but NEVER the request payload (card token leak risk).
                _breaker.RecordFailure();
                lastResult = new ChargeResult(ChargeOutcome.Failed, null, "processor_error");
                _logger.LogError(ex,
                    "Charge {IdempotencyKey} attempt {Attempt} threw {ExceptionType}.",
                    request.IdempotencyKey, attempt, ex.GetType().Name);
            }

            if (attempt < MaxAttempts)
            {
                // greybeard: jittered exponential backoff so retries don't synchronize into a thundering herd.
                var backoff = ComputeJitteredBackoff(attempt);
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
        }

        // greybeard: exhausted retries with an indeterminate outcome. Do NOT persist as terminal —
        // the idempotency key remains unresolved so a later reconciliation/retry can settle it safely.
        var failed = lastResult ?? new ChargeResult(ChargeOutcome.Failed, null, "unknown");
        _logger.LogError(
            "Charge {IdempotencyKey} failed after {MaxAttempts} attempts; final reason={DeclineReason}. " +
            "Left unresolved for reconciliation.",
            request.IdempotencyKey, MaxAttempts, failed.DeclineReason);
        return failed;
    }

    private static TimeSpan ComputeJitteredBackoff(int attempt)
    {
        // 250ms, 500ms, 1s ... capped, plus full jitter.
        var baseMs = Math.Min(250 * Math.Pow(2, attempt - 1), 4000);
        var jittered = Random.Shared.NextDouble() * baseMs;
        return TimeSpan.FromMilliseconds(jittered);
    }
}

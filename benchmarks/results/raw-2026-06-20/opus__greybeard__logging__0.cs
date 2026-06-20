using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// greybeard: rungs that apply -> 1 (money), 2 (mutation/idempotency), 3 (external call),
//            6 (half-way failure: charge may succeed at the bank but the response never reaches us).
//            Rungs 4 (concurrency) and 5 (list) do not apply here, skipped silently.

namespace Payments;

// greybeard: rung 1 -> money is an integer minor-units amount with its currency code attached.
//            No float anywhere near a balance.
public readonly record struct Money(long MinorUnits, string CurrencyCode)
{
    public override string ToString() => $"{MinorUnits} {CurrencyCode}"; // safe to log: no PAN here.
}

public enum ChargeOutcome
{
    Succeeded,
    Declined,    // definitive: bank said no. Safe to surface.
    Failed,      // definitive: our side / validation. No charge happened.
    Unknown      // greybeard: rung 6 -> we never learned the outcome. Money may have moved. Do NOT retry blindly.
}

public sealed record ChargeRequest(
    Money Amount,
    string PaymentMethodToken, // a vault token, never a raw PAN/CVV crossing this boundary.
    string IdempotencyKey);    // greybeard: rung 2 -> caller-supplied; identical key == identical charge.

public sealed record ChargeResult(
    ChargeOutcome Outcome,
    string? ProcessorChargeId,
    string? DeclineReason);

public interface ICardProcessor
{
    // Implementation is expected to forward the idempotency key to the upstream gateway.
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct);
}

public sealed class CardCharger
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan PerCallTimeout = TimeSpan.FromSeconds(8);

    private readonly ICardProcessor _processor;
    private readonly ILogger<CardCharger> _log;

    public CardCharger(ICardProcessor processor, ILogger<CardCharger> log)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<ChargeResult> ChargeCardAsync(ChargeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // greybeard: trust-boundary validation -> reject garbage before we touch the network.
        if (request.Amount.MinorUnits <= 0)
            throw new ArgumentException("Amount must be positive minor-units.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Amount.CurrencyCode) || request.Amount.CurrencyCode.Length != 3)
            throw new ArgumentException("Currency must be an ISO-4217 3-letter code.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PaymentMethodToken))
            throw new ArgumentException("Payment method token is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));

        // greybeard: no secrets in logs -> we log the idempotency key + amount, never the token, PAN, or CVV.
        //            IdempotencyKey is structured so failures are searchable later.
        using var scope = _log.BeginScope(new Dictionary<string, object>
        {
            ["idempotency_key"] = request.IdempotencyKey,
            ["amount"] = request.Amount.ToString(),
        });

        var rng = Random.Shared;
        ChargeResult? lastResult = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // greybeard: rung 6 -> log the attempt BEFORE the call. If the process dies mid-charge,
            //            this line is the breadcrumb proving we may have moved money.
            _log.LogInformation("Charge attempt {Attempt}/{Max} starting", attempt, MaxAttempts);

            // greybeard: rung 3 -> hard timeout on every external call, always.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PerCallTimeout);

            try
            {
                var result = await _processor
                    .ChargeAsync(request, timeoutCts.Token)
                    .ConfigureAwait(false);

                lastResult = result;

                // greybeard: rung 6 -> a decline is a definitive answer, not a transient fault. Stop, don't retry.
                if (result.Outcome is ChargeOutcome.Succeeded or ChargeOutcome.Declined or ChargeOutcome.Failed)
                {
                    _log.LogInformation(
                        "Charge attempt {Attempt} resolved: {Outcome} (processorChargeId={ChargeId}, declineReason={Reason})",
                        attempt, result.Outcome, result.ProcessorChargeId, result.DeclineReason);
                    return result;
                }

                _log.LogWarning("Charge attempt {Attempt} returned Unknown outcome", attempt);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancelled (not our timeout). The charge may be in-flight at the bank.
                _log.LogWarning("Charge cancelled by caller during attempt {Attempt}", attempt);
                throw;
            }
            catch (OperationCanceledException)
            {
                // greybeard: rung 3 + 6 -> our timeout fired. Outcome is UNKNOWN: the bank may have charged.
                //            The shared idempotency key makes a retry safe (exactly-once effect upstream).
                _log.LogWarning("Charge attempt {Attempt} timed out after {Timeout}s; outcome unknown",
                    attempt, PerCallTimeout.TotalSeconds);
                lastResult = new ChargeResult(ChargeOutcome.Unknown, null, "timeout");
            }
            catch (HttpRequestException ex)
            {
                // greybeard: no secrets in logs -> log the exception message/type, never the request body.
                _log.LogWarning(ex, "Charge attempt {Attempt} failed with transport error; outcome unknown", attempt);
                lastResult = new ChargeResult(ChargeOutcome.Unknown, null, "transport_error");
            }

            // greybeard: rung 3 -> retry only the Unknown/transient path, with jittered exponential backoff.
            //            Because we resend the SAME idempotency key, at-least-once delivery yields exactly-once effect.
            if (attempt < MaxAttempts)
            {
                var backoffMs = (int)(Math.Pow(2, attempt) * 100);
                var jitterMs = rng.Next(0, 100);
                var delay = TimeSpan.FromMilliseconds(backoffMs + jitterMs);
                _log.LogInformation("Backing off {Delay}ms before retry", delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        // greybeard: rung 6 -> exhausted retries with no definitive answer. Return Unknown, do NOT pretend success.
        //            Downstream reconciliation must resolve this against the processor using the idempotency key.
        var unresolved = lastResult ?? new ChargeResult(ChargeOutcome.Unknown, null, "exhausted_retries");
        _log.LogError(
            "Charge unresolved after {Max} attempts; outcome={Outcome}. Reconcile against processor by idempotency key.",
            MaxAttempts, unresolved.Outcome);
        return unresolved with { Outcome = ChargeOutcome.Unknown };
    }
}

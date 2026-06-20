using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Payments;

// greybeard: rungs that apply here -> 1 Money (charging a card), 2 Mutation (a charge
// must have exactly-once effect under retry), 3 External call (HTTP to the gateway).
// Rungs 4/5/6 do not apply: no local row contention, no list read, no multi-step saga
// inside this single call -- so no machinery is added for them.

// greybeard: rung 1 -- amount is integer minor units (cents), currency travels with it.
// Never a float/decimal-as-money-over-the-wire-without-context. No silent rounding here:
// the caller already rounded explicitly when it produced minor units.
public readonly record struct Money(long MinorUnits, string Currency)
{
    public static Money Of(long minorUnits, string currency)
    {
        if (minorUnits < 0)
            throw new ArgumentOutOfRangeException(nameof(minorUnits), "Charge amount cannot be negative.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO-4217 code.", nameof(currency));
        return new Money(minorUnits, currency.ToUpperInvariant());
    }
}

public sealed record ChargeRequest(
    Money Amount,
    string CardToken,        // greybeard: trust boundary -- a vault token, never a raw PAN. Never logged.
    string IdempotencyKey);  // greybeard: rung 2 -- stable, caller-supplied, survives retries.

public enum ChargeStatus { Succeeded, Declined, Failed }

public sealed record ChargeResult(ChargeStatus Status, string? GatewayChargeId, string? DeclineReason);

public sealed class CardCharger
{
    private readonly HttpClient _http;
    private readonly ILogger<CardCharger> _log;

    // greybeard: rung 3 -- a circuit breaker shared across calls. Once the gateway is
    // failing hard, we stop hammering it and fail fast until it recovers.
    private readonly CircuitBreaker _breaker;

    private const int MaxAttempts = 3;
    private static readonly TimeSpan PerCallTimeout = TimeSpan.FromSeconds(5);

    public CardCharger(HttpClient http, ILogger<CardCharger> log, CircuitBreaker breaker)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
    }

    public async Task<ChargeResult> ChargeAsync(ChargeRequest req, CancellationToken ct)
    {
        // greybeard: trust-boundary validation -- never trust input crossing into a money mutation.
        ArgumentNullException.ThrowIfNull(req);
        if (req.Amount.MinorUnits <= 0)
            throw new ArgumentException("Charge amount must be positive.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.CardToken))
            throw new ArgumentException("Card token is required.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required for a charge.", nameof(req));

        // greybeard: rung 3 -- circuit breaker. If open, fail fast; do not pile load on a sick gateway.
        if (!_breaker.AllowRequest())
        {
            _log.LogWarning("Charge short-circuited: gateway circuit is open. idemKey={IdempotencyKey}", req.IdempotencyKey);
            return new ChargeResult(ChargeStatus.Failed, null, "gateway_unavailable_circuit_open");
        }

        var wire = new GatewayChargeBody
        {
            // greybeard: rung 1 -- minor units + currency cross the wire together, no float anywhere.
            AmountMinor = req.Amount.MinorUnits,
            Currency = req.Amount.Currency,
            Source = req.CardToken,
        };

        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // greybeard: rung 3 -- ALWAYS a timeout. A hung gateway socket must not hang us forever.
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(PerCallTimeout);

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "charges")
            {
                Content = JsonContent.Create(wire),
            };
            // greybeard: rung 2 -- the idempotency key rides on EVERY attempt and every retry.
            // The gateway dedupes on it, so a retried/timed-out request produces exactly-once charge.
            httpReq.Headers.Add("Idempotency-Key", req.IdempotencyKey);

            try
            {
                using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);

                // 2xx -> definitive success. Charge happened exactly once (idem key guaranteed it).
                if (resp.IsSuccessStatusCode)
                {
                    var ok = await resp.Content.ReadFromJsonAsync<GatewayChargeResponse>(attemptCts.Token).ConfigureAwait(false);
                    _breaker.RecordSuccess();
                    // greybeard: rung 7 -- observable. We log the gateway charge id, the idem key, and
                    // attempt count. We NEVER log the card token, amount-as-secret, or response body raw.
                    _log.LogInformation(
                        "Charge succeeded. chargeId={ChargeId} idemKey={IdempotencyKey} attempt={Attempt}",
                        ok?.Id, req.IdempotencyKey, attempt);
                    return new ChargeResult(ChargeStatus.Succeeded, ok?.Id, null);
                }

                // greybeard: a decline (402) is a DEFINITIVE business answer, not a transient fault.
                // Retrying would not change it and could confuse the gateway -- stop here.
                if (resp.StatusCode == HttpStatusCode.PaymentRequired)
                {
                    var declined = await resp.Content.ReadFromJsonAsync<GatewayChargeResponse>(attemptCts.Token).ConfigureAwait(false);
                    _breaker.RecordSuccess(); // gateway is healthy; the *card* was declined.
                    _log.LogInformation(
                        "Charge declined. reason={Reason} idemKey={IdempotencyKey}",
                        declined?.DeclineCode ?? "unknown", req.IdempotencyKey);
                    return new ChargeResult(ChargeStatus.Declined, null, declined?.DeclineCode ?? "declined");
                }

                // 4xx (other than 402) -> our request is wrong. Retrying repeats the same mistake.
                if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500)
                {
                    _breaker.RecordSuccess(); // gateway responded fine; the bug is ours.
                    _log.LogError(
                        "Charge rejected by gateway as a client error. status={Status} idemKey={IdempotencyKey}",
                        (int)resp.StatusCode, req.IdempotencyKey);
                    return new ChargeResult(ChargeStatus.Failed, null, $"client_error_{(int)resp.StatusCode}");
                }

                // 5xx -> transient. Safe to retry BECAUSE the idempotency key makes it non-duplicating.
                _breaker.RecordFailure();
                lastTransient = new HttpRequestException($"Gateway returned {(int)resp.StatusCode}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled -- honor it, don't swallow.
            }
            catch (OperationCanceledException) // our per-attempt timeout fired
            {
                _breaker.RecordFailure();
                lastTransient = new TimeoutException($"Charge attempt {attempt} timed out after {PerCallTimeout.TotalSeconds}s.");
                _log.LogWarning("Charge attempt timed out. idemKey={IdempotencyKey} attempt={Attempt}", req.IdempotencyKey, attempt);
            }
            catch (HttpRequestException ex) // connection-level transient
            {
                _breaker.RecordFailure();
                lastTransient = ex;
                // greybeard: no secrets in logs -- we log the exception message, not request content/headers.
                _log.LogWarning("Charge attempt failed transiently. idemKey={IdempotencyKey} attempt={Attempt} error={Error}",
                    req.IdempotencyKey, attempt, ex.Message);
            }

            // greybeard: rung 3 -- jittered exponential backoff between retries. Bounded, never the last attempt.
            if (attempt < MaxAttempts)
            {
                var backoff = JitteredBackoff(attempt);
                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        // greybeard: exhausted retries on a TRANSIENT fault. We return Failed -- crucially NOT Declined.
        // The charge state is now genuinely UNKNOWN: it may or may not have landed at the gateway.
        // The caller must reconcile by querying the gateway with the SAME idempotency key before
        // ever re-charging. Returning a wrong "success" or a wrong "decline" here loses money.
        _log.LogError(lastTransient,
            "Charge unresolved after {Attempts} attempts -- state UNKNOWN, requires reconciliation. idemKey={IdempotencyKey}",
            MaxAttempts, req.IdempotencyKey);
        return new ChargeResult(ChargeStatus.Failed, null, "unresolved_requires_reconciliation");
    }

    private static TimeSpan JitteredBackoff(int attempt)
    {
        // base 200ms * 2^(attempt-1), plus full jitter, capped at 2s.
        var baseMs = Math.Min(200d * Math.Pow(2, attempt - 1), 2000d);
        var jittered = Random.Shared.NextDouble() * baseMs;
        return TimeSpan.FromMilliseconds(jittered);
    }
}

// --- wire contracts (kept minimal and explicit) ---

internal sealed class GatewayChargeBody
{
    [JsonPropertyName("amount_minor")] public long AmountMinor { get; init; }
    [JsonPropertyName("currency")] public string Currency { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
}

internal sealed class GatewayChargeResponse
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("decline_code")] public string? DeclineCode { get; init; }
}

// --- minimal circuit breaker (thread-safe, no external deps) ---

public sealed class CircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly object _gate = new();

    private int _consecutiveFailures;
    private DateTimeOffset _openedUntil = DateTimeOffset.MinValue;

    public CircuitBreaker(int failureThreshold = 5, TimeSpan? openDuration = null)
    {
        _failureThreshold = failureThreshold;
        _openDuration = openDuration ?? TimeSpan.FromSeconds(30);
    }

    public bool AllowRequest()
    {
        lock (_gate)
        {
            if (_openedUntil > DateTimeOffset.UtcNow) return false; // open -> fail fast
            return true; // closed or half-open -> let one through
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openedUntil = DateTimeOffset.MinValue;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
                _openedUntil = DateTimeOffset.UtcNow.Add(_openDuration);
        }
    }
}

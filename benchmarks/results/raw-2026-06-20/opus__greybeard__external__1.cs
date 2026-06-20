using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// greybeard rungs that apply here:
//   1. Money?            -> amount is integer minor units + ISO currency; never float.
//   2. Mutation?         -> idempotency key sent to gateway so a retry never double-charges.
//   3. External call?    -> hard timeout always; jittered backoff retry; circuit breaker on repeated failure.
//   6. Fail half-way?    -> timeout/5xx => outcome UNKNOWN, not "failed". Caller must reconcile, never silently re-charge.
//   7. Minimum correct + observable.

namespace Payments;

// greybeard (rung 1): money is integer minor units (e.g. cents), currency travels with it. No float anywhere.
public readonly record struct Money(long MinorUnits, string CurrencyCode)
{
    public Money
    {
        if (MinorUnits < 0) throw new ArgumentOutOfRangeException(nameof(MinorUnits), "amount must be non-negative");
        if (string.IsNullOrWhiteSpace(CurrencyCode) || CurrencyCode.Length != 3)
            throw new ArgumentException("currency must be a 3-letter ISO 4217 code", nameof(CurrencyCode));
    }
}

public enum ChargeStatus
{
    Succeeded,
    Declined,
    // greybeard (rung 6): the gateway may have charged the card even though we never got a clean answer.
    // This is NOT "failed" — surfacing it as failure invites a double-charge on retry.
    Unknown
}

public sealed record ChargeResult(ChargeStatus Status, string? GatewayChargeId, string? DeclineReason);

public sealed class CardChargeService
{
    private readonly HttpClient _http;
    private readonly ILogger<CardChargeService> _log;

    // greybeard (rung 3): bounded retries + jittered backoff. Don't hammer a struggling gateway.
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    // greybeard (rung 3): minimal circuit breaker — after enough consecutive failures, fail fast
    // instead of piling load onto a downstream that is clearly down.
    private const int BreakerThreshold = 5;
    private static readonly TimeSpan BreakerCooldown = TimeSpan.FromSeconds(30);
    private int _consecutiveFailures;
    private long _breakerOpenUntilTicks; // UTC ticks; 0 = closed

    public CardChargeService(HttpClient http, ILogger<CardChargeService> log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Charges a card via the external gateway.
    /// </summary>
    /// <param name="amount">Money in integer minor units + ISO currency.</param>
    /// <param name="cardToken">A gateway card token — NEVER a raw PAN (PCI scope + secrets-in-logs).</param>
    /// <param name="idempotencyKey">
    /// Stable, caller-owned key for THIS logical charge (e.g. the order/payment-attempt id).
    /// Must be identical across retries so the gateway dedupes and never double-charges.
    /// </param>
    public async Task<ChargeResult> ChargeAsync(
        Money amount,
        string cardToken,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        // greybeard: validate at the trust boundary before any side effect.
        if (string.IsNullOrWhiteSpace(cardToken)) throw new ArgumentException("cardToken required", nameof(cardToken));
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("idempotencyKey required", nameof(idempotencyKey));

        // greybeard (rung 3): circuit breaker open => fail fast, treat as UNKNOWN (we did not even ask).
        if (Volatile.Read(ref _breakerOpenUntilTicks) > DateTime.UtcNow.Ticks)
        {
            _log.LogWarning("Charge skipped: circuit breaker open. idempotencyKey={IdempotencyKey}", idempotencyKey);
            return new ChargeResult(ChargeStatus.Unknown, null, "circuit_open");
        }

        var payload = new
        {
            amount_minor = amount.MinorUnits,   // greybeard (rung 1): integer minor units on the wire, never a decimal/float.
            currency = amount.CurrencyCode,
            source = cardToken
            // greybeard: idempotency key goes in the header below, not the body, per gateway contract.
        };

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // greybeard (rung 3): per-attempt hard timeout, ALWAYS. Linked so caller cancellation still wins.
            using var timeoutCts = new CancellationTokenSource(RequestTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/charges")
            {
                Content = JsonContent.Create(payload)
            };
            // greybeard (rung 2): same idempotency key on every attempt => gateway collapses retries into one charge.
            request.Headers.Add("Idempotency-Key", idempotencyKey);

            try
            {
                using var response = await _http.SendAsync(request, linkedCts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var ok = await response.Content
                        .ReadFromJsonAsync<GatewayChargeResponse>(linkedCts.Token).ConfigureAwait(false);

                    RecordSuccess();
                    // greybeard: log the gateway charge id, never the token/amount-as-secret. No PAN, no secrets.
                    _log.LogInformation("Charge succeeded. idempotencyKey={IdempotencyKey} chargeId={ChargeId}",
                        idempotencyKey, ok?.Id);
                    return new ChargeResult(ChargeStatus.Succeeded, ok?.Id, null);
                }

                // greybeard: a clean 4xx decline is a definitive answer — do NOT retry, do NOT mark UNKNOWN.
                if (response.StatusCode is HttpStatusCode.PaymentRequired or HttpStatusCode.UnprocessableEntity
                    || ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.TooManyRequests))
                {
                    var declined = await response.Content
                        .ReadFromJsonAsync<GatewayChargeResponse>(linkedCts.Token).ConfigureAwait(false);
                    RecordSuccess(); // gateway answered cleanly; it is not "down".
                    _log.LogInformation("Charge declined. idempotencyKey={IdempotencyKey} reason={Reason}",
                        idempotencyKey, declined?.DeclineCode);
                    return new ChargeResult(ChargeStatus.Declined, null, declined?.DeclineCode ?? "declined");
                }

                // 429 / 5xx => transient. Retry (idempotency key keeps us safe).
                RecordFailure();
                _log.LogWarning("Charge transient gateway error {Status} on attempt {Attempt}. idempotencyKey={IdempotencyKey}",
                    (int)response.StatusCode, attempt, idempotencyKey);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                // greybeard (rung 6): timeout / connection drop. The charge MIGHT have gone through.
                // Retrying is safe ONLY because of the idempotency key. After last attempt => UNKNOWN, never silent-fail.
                RecordFailure();
                _log.LogWarning(ex, "Charge attempt {Attempt} failed (network/timeout). idempotencyKey={IdempotencyKey}",
                    attempt, idempotencyKey);
            }

            if (attempt < MaxAttempts)
            {
                // greybeard (rung 3): exponential backoff with full jitter to avoid retry stampedes.
                var backoffMs = (int)(Math.Pow(2, attempt) * 100);
                var jitterMs = Random.Shared.Next(0, backoffMs);
                await Task.Delay(backoffMs + jitterMs, cancellationToken).ConfigureAwait(false);
            }
        }

        // greybeard (rung 6): exhausted retries without a definitive answer. The card may or may not be charged.
        // Return UNKNOWN so the caller reconciles against the gateway (by idempotency key) instead of re-charging blind.
        _log.LogError("Charge outcome UNKNOWN after {Attempts} attempts — needs reconciliation. idempotencyKey={IdempotencyKey}",
            MaxAttempts, idempotencyKey);
        return new ChargeResult(ChargeStatus.Unknown, null, "no_definitive_answer");
    }

    private void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Volatile.Write(ref _breakerOpenUntilTicks, 0);
    }

    private void RecordFailure()
    {
        if (Interlocked.Increment(ref _consecutiveFailures) >= BreakerThreshold)
        {
            Volatile.Write(ref _breakerOpenUntilTicks, DateTime.UtcNow.Add(BreakerCooldown).Ticks);
        }
    }

    // greybeard: only the fields we trust/need. Never deserialize secrets back into logs.
    private sealed record GatewayChargeResponse(string? Id, string? DeclineCode);
}

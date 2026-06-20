using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// greybeard: rungs that apply here ->
//   1. Money?       charge amount is integer minor-units + currency code travels with it.
//   2. Mutation?    charging a card is a state mutation; caller-supplied idempotency key makes retries exactly-once.
//   3. External call? HTTP to the gateway: hard timeout, jittered backoff retry, circuit breaker on repeated failure.
//   7. Minimum correct code, observable: structured logs, no PAN/secrets in logs.
// Rungs 4 (concurrency), 5 (list), 6 (half-way saga) do not apply to a single synchronous charge -> skipped.

namespace Payments;

/// <summary>Amount carried as integer minor units (cents) with its currency. Never a float.</summary>
public readonly record struct Money(long MinorUnits, string Currency)
{
    // greybeard: rung 1 -- validate at the trust boundary; reject the nonsensical before it reaches the gateway.
    public void Validate()
    {
        if (MinorUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(MinorUnits), "Charge amount must be positive minor units.");
        if (string.IsNullOrWhiteSpace(Currency) || Currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO-4217 code.", nameof(Currency));
    }
}

public sealed record ChargeRequest(
    string CardToken,        // greybeard: a gateway token, NEVER a raw PAN. Raw card data never enters this process.
    Money Amount,
    string IdempotencyKey);  // greybeard: rung 2 -- caller owns this; same key => same charge, never a double-charge.

public enum ChargeOutcome { Succeeded, Declined, GatewayUnavailable }

public sealed record ChargeResult(ChargeOutcome Outcome, string? GatewayChargeId, string? DeclineReason);

/// <summary>Minimal circuit breaker: opens after N consecutive failures, half-opens after a cooldown.</summary>
public sealed class CircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _cooldown;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt = DateTimeOffset.MinValue;
    private readonly object _gate = new();

    public CircuitBreaker(int threshold = 5, TimeSpan? cooldown = null)
    {
        _threshold = threshold;
        _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
    }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                if (_consecutiveFailures < _threshold) return false;
                // greybeard: rung 3 -- half-open after cooldown so we don't hammer a dying gateway, but we do recover.
                if (DateTimeOffset.UtcNow - _openedAt >= _cooldown)
                {
                    _consecutiveFailures = _threshold - 1; // allow one trial request.
                    return false;
                }
                return true;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate) { _consecutiveFailures = 0; }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _threshold) _openedAt = DateTimeOffset.UtcNow;
        }
    }
}

public sealed class PaymentGatewayClient
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly CircuitBreaker _breaker;
    private readonly ILogger<PaymentGatewayClient> _log;

    public PaymentGatewayClient(HttpClient http, CircuitBreaker breaker, ILogger<PaymentGatewayClient> log)
    {
        _http = http;
        _breaker = breaker;
        _log = log;
    }

    /// <summary>
    /// Charges a card via the external gateway. Safe to retry with the same IdempotencyKey.
    /// Returns a result for the expected outcomes (success/decline/unavailable);
    /// throws only on programmer error (invalid input).
    /// </summary>
    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct = default)
    {
        // greybeard: rung 1 + trust boundary -- validate before we spend a network round-trip or a dollar.
        if (request is null) throw new ArgumentNullException(nameof(request));
        request.Amount.Validate();
        if (string.IsNullOrWhiteSpace(request.CardToken))
            throw new ArgumentException("CardToken is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required to make this charge retry-safe.", nameof(request));

        // greybeard: rung 3 -- if the breaker is open, fail fast. Don't queue work onto a gateway that's down.
        if (_breaker.IsOpen)
        {
            _log.LogWarning("Charge short-circuited; gateway circuit is open. idempotencyKey={Key}", request.IdempotencyKey);
            return new ChargeResult(ChargeOutcome.GatewayUnavailable, null, "circuit_open");
        }

        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // greybeard: rung 3 -- per-attempt hard timeout, linked to the caller's cancellation. No unbounded hang.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);

            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/charges")
                {
                    // greybeard: rung 2 -- the idempotency key rides as a header; the gateway dedupes the retry.
                    Content = JsonContent.Create(new
                    {
                        amount = request.Amount.MinorUnits,   // greybeard: rung 1 -- integer minor units on the wire.
                        currency = request.Amount.Currency,    // greybeard: rung 1 -- currency travels with the amount.
                        source = request.CardToken,
                    }),
                };
                httpRequest.Headers.Add("Idempotency-Key", request.IdempotencyKey);

                using var response = await _http.SendAsync(httpRequest, timeoutCts.Token).ConfigureAwait(false);

                // 2xx -> charge accepted by the gateway.
                if (response.IsSuccessStatusCode)
                {
                    _breaker.RecordSuccess();
                    var ok = await response.Content
                        .ReadFromJsonAsync<GatewayChargeResponse>(cancellationToken: ct)
                        .ConfigureAwait(false);
                    // greybeard: observability -- log the gateway charge id, never the card token or amount-in-the-clear secrets.
                    _log.LogInformation("Charge succeeded. chargeId={ChargeId} idempotencyKey={Key} attempt={Attempt}",
                        ok?.Id, request.IdempotencyKey, attempt);
                    return new ChargeResult(ChargeOutcome.Succeeded, ok?.Id, null);
                }

                // 402/422 -> a *business* decline (insufficient funds, etc.). Deterministic; retrying won't help.
                if (response.StatusCode is HttpStatusCode.PaymentRequired or HttpStatusCode.UnprocessableEntity)
                {
                    _breaker.RecordSuccess(); // the gateway is healthy; it just said no.
                    var declined = await response.Content
                        .ReadFromJsonAsync<GatewayChargeResponse>(cancellationToken: ct)
                        .ConfigureAwait(false);
                    _log.LogInformation("Charge declined. reason={Reason} idempotencyKey={Key}",
                        declined?.DeclineCode, request.IdempotencyKey);
                    return new ChargeResult(ChargeOutcome.Declined, null, declined?.DeclineCode ?? "declined");
                }

                // 4xx (other than the above) -> our request is malformed; retrying is pointless and hides bugs.
                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    _breaker.RecordSuccess();
                    _log.LogError("Charge rejected by gateway as a client error. status={Status} idempotencyKey={Key}",
                        (int)response.StatusCode, request.IdempotencyKey);
                    return new ChargeResult(ChargeOutcome.GatewayUnavailable, null, $"client_error_{(int)response.StatusCode}");
                }

                // 5xx / 429 -> transient; fall through to retry.
                _breaker.RecordFailure();
                lastTransient = new HttpRequestException($"Gateway returned {(int)response.StatusCode}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller asked to cancel -- honor it, don't swallow.
            }
            catch (OperationCanceledException)
            {
                // greybeard: rung 3 -- our own timeout fired. Transient: the charge may or may not have landed,
                // but the SAME idempotency key on the next attempt makes a retry safe (no double-charge).
                _breaker.RecordFailure();
                lastTransient = new TimeoutException($"Gateway timed out after {RequestTimeout.TotalSeconds}s.");
            }
            catch (HttpRequestException ex)
            {
                _breaker.RecordFailure();
                lastTransient = ex; // connection reset, DNS, etc. -- transient.
            }

            if (attempt < MaxAttempts)
            {
                // greybeard: rung 3 -- exponential backoff with full jitter; avoids retry storms / thundering herd.
                var backoffMs = (int)(Math.Pow(2, attempt) * 100);
                var jitterMs = Random.Shared.Next(0, backoffMs);
                _log.LogWarning("Charge attempt {Attempt} failed transiently; retrying. idempotencyKey={Key}",
                    attempt, request.IdempotencyKey);
                await Task.Delay(backoffMs + jitterMs, ct).ConfigureAwait(false);
            }
        }

        // greybeard: exhausted retries. We DO NOT know if the money moved. Return Unavailable so the caller
        // can reconcile (re-query the gateway by idempotency key) rather than assuming failure and re-charging blindly.
        _log.LogError(lastTransient,
            "Charge failed after {Attempts} attempts; outcome unknown -- reconciliation required. idempotencyKey={Key}",
            MaxAttempts, request.IdempotencyKey);
        return new ChargeResult(ChargeOutcome.GatewayUnavailable, null, "gateway_unavailable");
    }

    private sealed record GatewayChargeResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("decline_code")] string? DeclineCode);
}

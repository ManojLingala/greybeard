// Rungs that apply:
// 1. Money       -> amount in minor units (cents), currency code required, no float
// 2. Mutation    -> idempotency key required; safe to retry without double-charge
// 3. External    -> timeout on gateway call; jittered retry; structured error capture
// 6. Half-way    -> attempt logged before call; outcome updated after; partial failure is first-class

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public sealed record Money(long MinorUnits, string CurrencyCode)
{
    // greybeard: money is always integer minor-units (e.g. cents). No decimal, no float, ever.
    public override string ToString() => $"{MinorUnits} {CurrencyCode}";
}

public sealed record ChargeRequest(
    string IdempotencyKey,   // greybeard: caller supplies idempotency key; same key = same outcome, no double-charge
    string PaymentMethodId,
    Money Amount,
    string Description
);

public sealed record ChargeResult(
    bool Success,
    string? GatewayChargeId,
    string? FailureCode,
    string? FailureMessage
);

public sealed record ChargeAttemptLog(
    Guid AttemptId,
    string IdempotencyKey,
    Money Amount,
    DateTimeOffset AttemptedAt,
    bool? Success,
    string? GatewayChargeId,
    string? FailureCode,
    string? FailureMessage,
    long DurationMs
);

public interface IPaymentGateway
{
    // greybeard: external call — always accept a CancellationToken so callers can enforce a timeout
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct);
}

public interface IChargeAttemptStore
{
    // greybeard: mutation — persist attempt record before the external call so failures are recoverable
    Task<bool> TryRecordAttemptAsync(ChargeAttemptLog log, CancellationToken ct);
    Task UpdateAttemptOutcomeAsync(Guid attemptId, ChargeResult result, long durationMs, CancellationToken ct);
    Task<ChargeAttemptLog?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);
}

public sealed class CardChargeService
{
    private static readonly TimeSpan GatewayTimeout = TimeSpan.FromSeconds(10);
    private static readonly int MaxRetries = 2;
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMilliseconds(200);

    private readonly IPaymentGateway _gateway;
    private readonly IChargeAttemptStore _store;
    private readonly ILogger<CardChargeService> _logger;
    private readonly Random _rng = new();

    public CardChargeService(
        IPaymentGateway gateway,
        IChargeAttemptStore store,
        ILogger<CardChargeService> logger)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _store   = store   ?? throw new ArgumentNullException(nameof(store));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Charges a card and logs the attempt so failures can be debugged later.
    /// </summary>
    public async Task<ChargeResult> ChargeCardAsync(ChargeRequest request, CancellationToken outerCt = default)
    {
        // greybeard: trust-boundary validation — never accept garbage from the caller
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PaymentMethodId))
            throw new ArgumentException("PaymentMethodId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Amount.CurrencyCode))
            throw new ArgumentException("CurrencyCode is required.", nameof(request));
        // greybeard: money — reject non-positive amounts; zero or negative is a logic error, not a gateway problem
        if (request.Amount.MinorUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Amount must be positive minor-units.");

        // greybeard: idempotency — if we already have a completed attempt for this key, return it without calling the gateway again
        var existing = await _store.FindByIdempotencyKeyAsync(request.IdempotencyKey, outerCt);
        if (existing?.Success is not null)
        {
            _logger.LogInformation(
                "Idempotent replay for key {IdempotencyKey}; returning stored outcome Success={Success}",
                request.IdempotencyKey, existing.Success);

            return new ChargeResult(
                existing.Success.Value,
                existing.GatewayChargeId,
                existing.FailureCode,
                existing.FailureMessage);
        }

        var attemptId    = Guid.NewGuid();
        var attemptedAt  = DateTimeOffset.UtcNow;

        // greybeard: log attempt BEFORE the external call so a crash mid-flight is still observable
        var pendingLog = new ChargeAttemptLog(
            AttemptId:       attemptId,
            IdempotencyKey:  request.IdempotencyKey,
            Amount:          request.Amount,
            AttemptedAt:     attemptedAt,
            Success:         null,          // not yet known
            GatewayChargeId: null,
            FailureCode:     null,
            FailureMessage:  null,
            DurationMs:      0);

        await _store.TryRecordAttemptAsync(pendingLog, outerCt);

        // greybeard: no secrets in logs — log the key and payment method ID (opaque tokens), not card numbers or CVCs
        _logger.LogInformation(
            "Charging card: AttemptId={AttemptId} IdempotencyKey={IdempotencyKey} Amount={Amount}",
            attemptId, request.IdempotencyKey, request.Amount);

        ChargeResult result;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // greybeard: external call — enforce a hard timeout; jittered exponential backoff on transient failures
        result = await CallWithRetryAsync(request, outerCt);

        sw.Stop();

        // greybeard: persist outcome so failures can be debugged; duration recorded for latency analysis
        await _store.UpdateAttemptOutcomeAsync(attemptId, result, sw.ElapsedMilliseconds, outerCt);

        if (result.Success)
        {
            _logger.LogInformation(
                "Charge succeeded: AttemptId={AttemptId} GatewayChargeId={GatewayChargeId} DurationMs={DurationMs}",
                attemptId, result.GatewayChargeId, sw.ElapsedMilliseconds);
        }
        else
        {
            // greybeard: structured failure log — FailureCode is machine-readable for alerting; message helps humans debug
            _logger.LogWarning(
                "Charge failed: AttemptId={AttemptId} FailureCode={FailureCode} DurationMs={DurationMs}",
                attemptId, result.FailureCode, sw.ElapsedMilliseconds);
            // greybeard: FailureMessage deliberately omitted from the log line above — it may contain PII from the gateway
        }

        return result;
    }

    private async Task<ChargeResult> CallWithRetryAsync(ChargeRequest request, CancellationToken outerCt)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            // greybeard: external call — link timeout to each individual attempt, not the whole retry loop
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            cts.CancelAfter(GatewayTimeout);

            try
            {
                var result = await _gateway.ChargeAsync(request, cts.Token);

                // greybeard: only retry transient/network failures; decline codes are final — retrying them wastes money and annoys customers
                if (!result.Success && IsRetryable(result.FailureCode) && attempt <= MaxRetries)
                {
                    var delay = BackoffDelay(attempt);
                    _logger.LogWarning(
                        "Transient gateway failure (attempt {Attempt}/{Max}), retrying in {DelayMs}ms. Code={FailureCode}",
                        attempt, MaxRetries + 1, delay.TotalMilliseconds, result.FailureCode);
                    await Task.Delay(delay, outerCt);
                    continue;
                }

                return result;
            }
            catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
            {
                // greybeard: gateway timed out, not the caller — treat as a transient failure
                if (attempt <= MaxRetries)
                {
                    var delay = BackoffDelay(attempt);
                    _logger.LogWarning(
                        "Gateway timeout (attempt {Attempt}/{Max}), retrying in {DelayMs}ms",
                        attempt, MaxRetries + 1, delay.TotalMilliseconds);
                    await Task.Delay(delay, outerCt);
                    continue;
                }

                // greybeard: partial failure is a first-class path — return a structured result instead of letting an exception escape
                return new ChargeResult(
                    Success:         false,
                    GatewayChargeId: null,
                    FailureCode:     "gateway_timeout",
                    FailureMessage:  "The payment gateway did not respond within the allowed window.");
            }
        }
    }

    private static bool IsRetryable(string? code) =>
        code is "network_error" or "gateway_timeout" or "service_unavailable" or "rate_limited";

    private TimeSpan BackoffDelay(int attempt)
    {
        // greybeard: jitter prevents thundering herd when many retries fire simultaneously
        var exponential = TimeSpan.FromMilliseconds(BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var jitter      = TimeSpan.FromMilliseconds(_rng.NextDouble() * 100);
        return exponential + jitter;
    }
}

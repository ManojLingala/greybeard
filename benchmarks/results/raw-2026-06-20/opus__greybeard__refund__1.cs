using System;

/// <summary>
/// A monetary amount represented in integer minor units (e.g. cents) with its currency.
/// greybeard: rung 1 (money) -> never float; currency code travels WITH the amount so we
/// can never silently mix or compare incompatible currencies.
/// </summary>
public readonly record struct Money(long MinorUnits, string Currency)
{
    public static Money Of(long minorUnits, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency code is required.", nameof(currency));
        // greybeard: trust-boundary validation -> normalize/validate the currency code (ISO 4217 is 3 letters).
        if (currency.Length != 3)
            throw new ArgumentException("Currency must be a 3-letter ISO 4217 code.", nameof(currency));
        return new Money(minorUnits, currency.ToUpperInvariant());
    }
}

public static class Refunds
{
    /// <summary>
    /// Calculates a 30% refund on an order total.
    /// </summary>
    /// <param name="orderTotal">The full order total in integer minor units (e.g. cents).</param>
    /// <param name="rounding">
    /// How to resolve the fractional minor unit. Defaults to banker's rounding to avoid
    /// systematic bias over many refunds.
    /// </param>
    /// <returns>The refund amount, in the same currency, in integer minor units.</returns>
    public static Money ThirtyPercentRefund(
        Money orderTotal,
        MidpointRounding rounding = MidpointRounding.ToEven)
    {
        // greybeard: trust-boundary validation -> a negative order total is nonsense for a refund;
        // reject it rather than emit a "negative refund" (a stealth credit/charge).
        if (orderTotal.MinorUnits < 0)
            throw new ArgumentOutOfRangeException(
                nameof(orderTotal), "Order total must be non-negative.");

        // greybeard: rung 1 (money) -> compute 30% in EXACT decimal arithmetic, never double/float.
        // 30% of an integer total can land on a half-cent; we make the rounding choice EXPLICIT
        // (no silent truncation) so the missing half-cent has a documented home.
        decimal refundExact = orderTotal.MinorUnits * 0.30m;
        long refundMinorUnits = (long)Math.Round(refundExact, 0, rounding);

        // greybeard: rung 1 (money) -> result carries the SAME currency as the input; the refund
        // can never silently change or drop the currency.
        return new Money(refundMinorUnits, orderTotal.Currency);
    }
}

/* greybeard re-read at 3am: "What still breaks?"
 *   - Money is integer minor units throughout; no float ever touches the amount. OK.
 *   - The half-cent on a 30% split is rounded EXPLICITLY (banker's by default), not truncated. OK.
 *   - Currency travels with the amount and is validated at the boundary. OK.
 *   - Negative totals are rejected, not silently turned into reverse-charges. OK.
 *   - This is a PURE calculation: no mutation, no external call, no concurrency, no list, no
 *     partial-failure path -> rungs 2-6 deliberately skipped. The CALLER that actually issues
 *     the refund still owes rung 2 (idempotency key so a retried refund never double-credits)
 *     and rung 3 (timeout + backoff on the PSP call). Flagged for that layer, not this one.
 */

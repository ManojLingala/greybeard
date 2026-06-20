using System;

public static class RefundCalculator
{
    // greybeard rungs that apply: (1) Money -> integer minor-units, explicit rounding,
    // currency travels with the amount. Rungs 2-6 (mutation/external/concurrency/list/
    // partial-failure) do NOT apply: this is a pure calculation, so no machinery added.

    /// <summary>
    /// Calculates a 30% refund on an order total.
    /// </summary>
    /// <param name="orderTotalMinorUnits">
    /// The order total in integer minor units (e.g. cents for USD, pence for GBP).
    /// greybeard: money is integer minor-units, never float — no half-cents drift.
    /// </param>
    /// <param name="currency">
    /// ISO-4217 currency code. greybeard: currency travels with the amount so a refund
    /// can never be silently issued in the wrong denomination.
    /// </param>
    /// <returns>The refund amount as a Money value in the same currency.</returns>
    public static Money CalculateRefund(long orderTotalMinorUnits, string currency)
    {
        // greybeard: trust-boundary validation — reject inputs that make no financial sense.
        if (orderTotalMinorUnits < 0)
            throw new ArgumentOutOfRangeException(
                nameof(orderTotalMinorUnits),
                "Order total cannot be negative.");

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            throw new ArgumentException(
                "Currency must be a 3-letter ISO-4217 code.", nameof(currency));

        // greybeard: do the math in integer space. 30% = total * 30 / 100.
        // Multiply BEFORE dividing so we keep full precision and decide rounding ourselves —
        // never let the language silently truncate the half-cent into the void.
        // Use checked arithmetic so a pathological total surfaces as an exception, not a
        // silent overflow that hands the customer a fortune.
        long numerator = checked(orderTotalMinorUnits * 30L);

        // greybeard: explicit rounding policy — round half away from zero, the conventional
        // choice for refunds (customer-favourable on the .5 boundary). Documented, not implicit.
        long quotient = numerator / 100L;
        long remainder = numerator % 100L;
        long refundMinorUnits = (remainder * 2 >= 100) ? quotient + 1 : quotient;

        return new Money(refundMinorUnits, currency.ToUpperInvariant());
    }
}

/// <summary>
/// greybeard: a Money type so an amount can never be passed around as a bare float/decimal
/// detached from its currency. Minor units are integer by construction.
/// </summary>
public readonly struct Money
{
    public long MinorUnits { get; }
    public string Currency { get; }

    public Money(long minorUnits, string currency)
    {
        MinorUnits = minorUnits;
        Currency = currency ?? throw new ArgumentNullException(nameof(currency));
    }

    public override string ToString() => $"{MinorUnits} {Currency} (minor units)";
}

// greybeard re-read at 3am: "What still breaks?"
//  - No float anywhere; rounding is explicit and customer-favourable. OK.
//  - Currency is bound to the amount and normalized. OK.
//  - Overflow is checked, not silent. OK.
//  - This is a pure function: no DB write, no external call, no retry — so no idempotency
//    key or timeout is added here. Those belong to the CALLER that actually issues the
//    refund (a mutation + external payment-provider call). Flagged for that layer, not faked here.

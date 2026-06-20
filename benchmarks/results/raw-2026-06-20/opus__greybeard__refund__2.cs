using System;

/// <summary>
/// Computes refunds as a fixed percentage of an order total.
/// greybeard: Money rung applies (rungs 2-6 do not -- this is a pure calc:
/// no mutation, no external call, no concurrency, no list, no partial failure).
/// </summary>
public static class RefundCalculator
{
    // greybeard: Money -> amount is integer minor-units (e.g. cents), never a float/decimal of dollars.
    //            Currency code travels with the amount so we never refund 100 USD as 100 JPY.
    public readonly record struct Money(long MinorUnits, string Currency)
    {
        public Money
        {
            // greybeard: trust-boundary validation -- reject nonsense before it propagates.
            if (MinorUnits < 0)
                throw new ArgumentOutOfRangeException(nameof(MinorUnits), "Amount cannot be negative.");
            if (string.IsNullOrWhiteSpace(Currency) || Currency.Length != 3)
                throw new ArgumentException("Currency must be a 3-letter ISO 4217 code.", nameof(Currency));
            Currency = Currency.ToUpperInvariant();
        }
    }

    /// <summary>
    /// Calculates a 30% refund on the given order total.
    /// </summary>
    /// <param name="orderTotal">The order total in integer minor-units, with its currency.</param>
    /// <returns>The refund amount in the same currency, in integer minor-units.</returns>
    public static Money CalculateRefund(Money orderTotal)
    {
        const int RefundPercent = 30;

        // greybeard: Money -> all arithmetic stays in integer space. We compute
        //            (total * 30) / 100. No floating point can ever touch the value.
        //
        // greybeard: explicit rounding -- 30% of an odd amount loses a fraction of a
        //            minor unit. We round HALF-UP deterministically instead of letting
        //            integer truncation silently swallow the half-cent. Document the choice:
        //            the customer is favoured on the rounding boundary.
        //
        //            Example: 999 cents * 30 = 29970; 29970 / 100 = 299.7 -> 300 cents.
        long numerator = orderTotal.MinorUnits * RefundPercent;            // safe: long, no overflow for realistic totals
        long refundMinorUnits = (numerator + 50) / 100;                    // +50 = +half-divisor for round-half-up

        // greybeard: Currency travels with the amount through the result, unchanged.
        return new Money(refundMinorUnits, orderTotal.Currency);
    }
}

// greybeard: Rung 1 (Money) -- order total in integer minor-units (cents); currency code travels with amount.
// greybeard: Rung 6 (Can it fail half-way?) -- validation throws before any mutation; caller owns transaction boundary.

using System;

/// <summary>
/// Represents a monetary amount in minor units (e.g. cents) with an explicit currency code.
/// Never use floating-point for money.
/// </summary>
public readonly struct Money
{
    public long AmountMinorUnits { get; }
    public string CurrencyCode { get; }

    public Money(long amountMinorUnits, string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
            throw new ArgumentException("Currency code must not be empty.", nameof(currencyCode));

        AmountMinorUnits = amountMinorUnits;
        CurrencyCode = currencyCode.ToUpperInvariant();
    }

    public override string ToString() => $"{AmountMinorUnits} {CurrencyCode}";
}

public static class RefundCalculator
{
    // greybeard: Rung 1 (Money) -- 30% expressed as integer ratio (30/100) to avoid float arithmetic.
    // Integer division truncates toward zero; we floor (favour the merchant, not the customer)
    // and document that choice explicitly so it is never a surprise.
    private const long RefundNumerator   = 30L;
    private const long RefundDenominator = 100L;

    /// <summary>
    /// Calculates a 30% refund on <paramref name="orderTotal"/>.
    ///
    /// Rounding: truncating integer division (floor toward zero) -- refund is rounded DOWN
    /// to the nearest minor unit. This means the merchant never refunds more than 30%.
    /// Document this contract in your API spec; change it only deliberately.
    /// </summary>
    /// <param name="orderTotal">
    ///   The original order total in minor units (e.g. cents for USD).
    ///   Must be non-negative; negative totals indicate a data/logic error upstream.
    /// </param>
    /// <returns>
    ///   A <see cref="Money"/> representing the refund amount, in the same currency and minor units.
    /// </returns>
    /// <exception cref="ArgumentException">
    ///   Thrown when <paramref name="orderTotal"/> carries a negative amount, preventing
    ///   a silent negative-refund (i.e. an accidental charge).
    /// </exception>
    public static Money CalculateThirtyPercentRefund(Money orderTotal)
    {
        // greybeard: trust-boundary validation -- reject nonsensical input before touching numbers.
        if (orderTotal.AmountMinorUnits < 0)
            throw new ArgumentException(
                "Order total must not be negative. A negative total would produce a negative refund " +
                "(an implicit charge). Investigate upstream data.",
                nameof(orderTotal));

        // greybeard: Rung 1 (Money) -- pure integer arithmetic; no float, no decimal cast.
        // Multiply first, then divide, to preserve precision within long range.
        // Max safe order total: long.MaxValue / 30 ≈ 307,445,734,561,825,860 minor units (~$3 quadrillion).
        // If amounts can exceed that, switch to BigInteger or validate an upper bound here.
        long refundMinorUnits = (orderTotal.AmountMinorUnits * RefundNumerator) / RefundDenominator;

        return new Money(refundMinorUnits, orderTotal.CurrencyCode);
    }
}

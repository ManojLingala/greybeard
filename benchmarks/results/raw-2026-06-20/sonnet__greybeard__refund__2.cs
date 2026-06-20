// greybeard: Rungs that apply here:
// 1. Money? YES — order total is currency; must use integer minor-units, never float.
// 6. Can it fail half-way? Flagged — caller is responsible for persisting the refund amount
//    atomically with order state; this method is pure calculation only.

using System;

/// <summary>
/// Calculates a 30% refund amount for an order total.
/// All amounts are in integer minor-units (e.g., cents for USD) to avoid floating-point errors.
/// </summary>
public static class RefundCalculator
{
    private const int RefundBasisPoints = 3000; // 30.00% expressed as basis points (100 bp = 1%)
    private const int BasisPointDivisor = 10000;

    /// <summary>
    /// Calculates a 30% refund on the given order total.
    /// </summary>
    /// <param name="orderTotalMinorUnits">
    /// The order total in integer minor-units (e.g., cents). Must be non-negative.
    /// </param>
    /// <param name="currencyCode">
    /// ISO 4217 currency code (e.g., "USD"). Travels with the amount so the caller
    /// cannot silently mix currencies.
    /// </param>
    /// <returns>
    /// A <see cref="MoneyAmount"/> containing the refund amount in minor-units and the
    /// original currency code. Rounding is floor (truncate toward zero) — the merchant
    /// never refunds more than 30%; any remainder stays with the merchant.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="orderTotalMinorUnits"/> is negative.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="currencyCode"/> is null or whitespace.
    /// </exception>
    public static MoneyAmount CalculateThirtyPercentRefund(
        long orderTotalMinorUnits,
        string currencyCode)
    {
        // greybeard: Money — validate inputs at the trust boundary before any arithmetic.
        if (orderTotalMinorUnits < 0)
            throw new ArgumentOutOfRangeException(
                nameof(orderTotalMinorUnits),
                "Order total must be non-negative.");

        if (string.IsNullOrWhiteSpace(currencyCode))
            throw new ArgumentException(
                "Currency code must be provided.", nameof(currencyCode));

        // greybeard: Money — integer arithmetic only; no float, no double, no decimal division
        // that could silently accumulate rounding error.
        // 30% = 3000 basis points. Multiply first, then divide to preserve precision.
        // Floor division: any sub-unit fraction is truncated (merchant keeps the rounding diff).
        long refundMinorUnits = (orderTotalMinorUnits * RefundBasisPoints) / BasisPointDivisor;

        // greybeard: Money — currency code travels with the result so callers cannot
        // accidentally apply a USD refund to a JPY (zero-decimal) order.
        return new MoneyAmount(refundMinorUnits, currencyCode.ToUpperInvariant());
    }
}

/// <summary>
/// Represents a monetary amount in integer minor-units paired with its ISO 4217 currency code.
/// </summary>
public readonly struct MoneyAmount : IEquatable<MoneyAmount>
{
    /// <summary>Amount in minor-units (e.g., cents for USD, yen for JPY).</summary>
    public long MinorUnits { get; }

    /// <summary>ISO 4217 currency code, e.g. "USD", "EUR", "JPY".</summary>
    public string CurrencyCode { get; }

    public MoneyAmount(long minorUnits, string currencyCode)
    {
        MinorUnits = minorUnits;
        CurrencyCode = currencyCode ?? throw new ArgumentNullException(nameof(currencyCode));
    }

    public bool Equals(MoneyAmount other) =>
        MinorUnits == other.MinorUnits &&
        string.Equals(CurrencyCode, other.CurrencyCode, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is MoneyAmount m && Equals(m);
    public override int GetHashCode() => HashCode.Combine(MinorUnits, CurrencyCode.ToUpperInvariant());
    public override string ToString() => $"{MinorUnits} {CurrencyCode}";
}

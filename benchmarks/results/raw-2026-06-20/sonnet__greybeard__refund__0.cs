// greybeard: Rung 1 (Money) — amount in integer minor-units (e.g. cents), currency code travels with the value, no float arithmetic.
// greybeard: Rung 6 (Partial failure) — returns a typed result; caller decides whether to persist/apply the refund amount.

using System;

public readonly record struct Money(long MinorUnits, string CurrencyCode)
{
    public override string ToString() => $"{MinorUnits} {CurrencyCode}";
}

public readonly record struct RefundResult(
    Money RefundAmount,
    Money OrderTotal,
    long RefundBasisPoints,   // greybeard: store the rate used so the ledger entry is self-describing
    string Note);

public static class RefundCalculator
{
    private const long RefundRateBasisPoints = 3_000; // 30.00 % expressed in basis points — no magic floats
    private const long BasisPointDivisor    = 10_000;

    /// <summary>
    /// Calculates a 30 % refund on <paramref name="orderTotal"/>.
    /// Uses integer arithmetic on minor units (e.g. cents) to avoid floating-point rounding errors.
    /// Truncates sub-cent remainders toward zero and records them in the result note so nothing
    /// disappears silently — the caller must decide what to do with any remainder.
    /// </summary>
    /// <param name="orderTotal">Order total in minor units with its currency code.</param>
    /// <returns>A <see cref="RefundResult"/> describing the refund; throws on invalid input.</returns>
    public static RefundResult Calculate30PctRefund(Money orderTotal)
    {
        // greybeard: Rung 1 — trust-boundary validation; never let a negative or zero order total
        //            silently produce a nonsensical refund.
        if (string.IsNullOrWhiteSpace(orderTotal.CurrencyCode))
            throw new ArgumentException("Currency code must be provided.", nameof(orderTotal));

        if (orderTotal.MinorUnits < 0)
            throw new ArgumentOutOfRangeException(nameof(orderTotal),
                "Order total cannot be negative.");

        if (orderTotal.MinorUnits == 0)
        {
            // greybeard: Rung 6 — zero-total is valid (fully discounted orders exist); return zero refund.
            return new RefundResult(
                RefundAmount     : new Money(0, orderTotal.CurrencyCode),
                OrderTotal       : orderTotal,
                RefundBasisPoints: RefundRateBasisPoints,
                Note             : "Order total is zero; refund is zero.");
        }

        // greybeard: Rung 1 — integer-only arithmetic; no float, no decimal division that could drift.
        //            30 % = 3000 basis points. Multiply first, then divide to preserve precision.
        long rawMinorUnits  = orderTotal.MinorUnits * RefundRateBasisPoints;
        long refundAmount   = rawMinorUnits / BasisPointDivisor;          // truncate toward zero
        long remainderUnits = rawMinorUnits % BasisPointDivisor;          // greybeard: never discard silently

        string note = remainderUnits == 0
            ? "Exact 30 % refund; no sub-unit remainder."
            : $"30 % refund truncated; sub-unit remainder of {remainderUnits} basis-point-units was dropped. Caller should handle remainder per business rules.";

        // greybeard: Rung 1 — currency code propagates with the refund amount so the caller never
        //            has to guess which currency the minor units belong to.
        return new RefundResult(
            RefundAmount     : new Money(refundAmount, orderTotal.CurrencyCode),
            OrderTotal       : orderTotal,
            RefundBasisPoints: RefundRateBasisPoints,
            Note             : note);
    }
}

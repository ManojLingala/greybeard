using System;

public class RefundCalculator
{
    /// <summary>
    /// Calculates a 30% refund on an order total.
    /// greybeard: Money? Use integer minor-units (cents), never float. Explicit rounding. Currency code travels with amount.
    /// greybeard: Can it fail half-way? Rounding must be deterministic and lossless.
    /// </summary>
    /// <param name="orderTotalMinorUnits">Order total in minor units (e.g., cents for USD). Must be non-negative.</param>
    /// <param name="currencyCode">ISO 4217 currency code (e.g., "USD", "EUR"). Required for audit trail.</param>
    /// <returns>Refund amount in minor units, rounded down per payment processor conventions.</returns>
    /// <exception cref="ArgumentException">Thrown if orderTotalMinorUnits is negative or currencyCode is null/empty.</exception>
    public static long CalculateThirtyPercentRefund(long orderTotalMinorUnits, string currencyCode)
    {
        // greybeard: Money? Validate trust boundary - caller must supply valid currency and amount.
        if (orderTotalMinorUnits < 0)
        {
            throw new ArgumentException("Order total cannot be negative.", nameof(orderTotalMinorUnits));
        }

        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new ArgumentException("Currency code is required.", nameof(currencyCode));
        }

        // greybeard: Money? Use integer arithmetic. 30% = 30/100. Multiply first, then divide to preserve precision.
        // Rounding DOWN (truncation) is standard for refunds to avoid over-refunding due to banker's rounding.
        long refundMinorUnits = (orderTotalMinorUnits * 30) / 100;

        // greybeard: Mutation? This calculation is pure; the caller is responsible for idempotency when persisting.
        // Log the calculation for audit (caller must strip secrets, we return the bare amount).
        System.Diagnostics.Debug.WriteLine(
            $"Refund calculated: original={orderTotalMinorUnits} {currencyCode}, refund={refundMinorUnits} {currencyCode}"
        );

        return refundMinorUnits;
    }
}

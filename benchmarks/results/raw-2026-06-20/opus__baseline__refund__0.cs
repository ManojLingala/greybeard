```csharp
using System;

/// <summary>
/// Calculates a fixed 30% refund on an order total.
/// </summary>
public static class RefundCalculator
{
    private const decimal RefundRate = 0.30m;

    /// <summary>
    /// Calculates a 30% refund for the given order total.
    /// </summary>
    /// <param name="orderTotal">The total amount of the order. Must be non-negative.</param>
    /// <returns>The refund amount, rounded to 2 decimal places (banker's rounding).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="orderTotal"/> is negative.</exception>
    public static decimal CalculateRefund(decimal orderTotal)
    {
        if (orderTotal < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderTotal),
                orderTotal,
                "Order total cannot be negative.");
        }

        return Math.Round(orderTotal * RefundRate, 2, MidpointRounding.ToEven);
    }
}
```
```csharp
using System;

public static class RefundCalculator
{
    /// <summary>
    /// Calculates a 30% refund on the given order total.
    /// </summary>
    /// <param name="orderTotal">The total amount of the order. Must be non-negative.</param>
    /// <returns>The refund amount, equal to 30% of the order total, rounded to 2 decimal places.</returns>
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

        const decimal RefundRate = 0.30m;
        return Math.Round(orderTotal * RefundRate, 2, MidpointRounding.AwayFromZero);
    }
}
```
public static class OrderRefundCalculator
{
    /// <summary>
    /// Calculates a 30% refund amount on the given order total.
    /// </summary>
    /// <param name="orderTotal">The original order total.</param>
    /// <returns>The refund amount (30% of the order total).</returns>
    public static decimal CalculateRefund(decimal orderTotal)
    {
        const decimal refundRate = 0.30m;
        return orderTotal * refundRate;
    }
}

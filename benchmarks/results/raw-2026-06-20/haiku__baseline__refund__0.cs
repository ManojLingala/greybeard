public class RefundCalculator
{
    /// <summary>
    /// Calculates a 30% refund on an order total.
    /// </summary>
    /// <param name="orderTotal">The total amount of the order</param>
    /// <returns>The refund amount (30% of order total)</returns>
    public decimal CalculateRefund(decimal orderTotal)
    {
        const decimal RefundPercentage = 0.30m;
        return orderTotal * RefundPercentage;
    }
}

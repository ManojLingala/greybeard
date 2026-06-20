```csharp
public static decimal CalculateRefund(decimal orderTotal)
{
    const decimal refundRate = 0.30m;
    return Math.Round(orderTotal * refundRate, 2);
}
```

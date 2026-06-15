# 1. Refund calculator — the half-cent that walks away

**Task:** "Write a method that refunds 30% of an order total."

### Without greybeard

```csharp
public double CalculateRefund(double orderTotal, double percent)
{
    return orderTotal * (percent / 100.0);
}
// CalculateRefund(19.99, 30) => 5.997  -> stored/charged as 5.997 or 6.00 depending on the sink
```

`double` cannot represent `19.99` exactly. Across thousands of refunds the
fractions drift, your ledger stops reconciling, and finance opens a ticket with
your name on it.

### With greybeard

```csharp
// greybeard[1:money]: amounts are int64 minor units (cents). No floating point on money, ever.
// greybeard[1:money]: rounding is explicit (banker's rounding) and the rule is named, not implicit.
public long CalculateRefundMinor(long orderTotalMinor, int percentBps)
{
    // percentBps = basis points (30% => 3000) so the rate itself is integer-exact.
    // Round half-to-even to avoid systematic bias across many refunds.
    var raw = (decimal)orderTotalMinor * percentBps / 10_000m;
    return (long)Math.Round(raw, MidpointRounding.ToEven);
}
// CalculateRefundMinor(1999, 3000) => 600  (i.e. $6.00, deterministic)
```

**Rung:** 1 (money). **3am page saved:** the finance reconciliation that never balances.

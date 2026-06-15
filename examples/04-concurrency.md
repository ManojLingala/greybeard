# 4. Decrement inventory — the lost update that oversells

**Task:** "Decrement stock when an order is placed."

### Without greybeard

```csharp
public async Task Reserve(int productId, int qty)
{
    var product = await _db.Products.FindAsync(productId);  // read
    product.Stock -= qty;                                   // modify in memory
    await _db.SaveChangesAsync();                           // write back
}
```

Two orders for the last unit run at the same time. Both read `Stock = 1`, both
write `Stock = 0`, both succeed. You just sold the same unit twice. Read-modify-write
without protection is a textbook lost update.

### With greybeard

```csharp
// greybeard[4:concurrency]: no read-modify-write race. Conditional update is atomic in the DB,
// not in app memory. Optimistic: the WHERE guards the invariant (stock never goes negative).
public async Task<bool> Reserve(int productId, int qty, CancellationToken ct)
{
    var rows = await _db.Products
        .Where(p => p.Id == productId && p.Stock >= qty)
        .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock - qty), ct);

    // rows == 0 means another transaction won, or stock insufficient.
    return rows == 1;  // greybeard[6:partial]: caller treats false as "out of stock", a normal path
}
```

(For multi-row invariants, escalate to a `rowversion` concurrency token with a
retry loop, or a `SELECT ... FOR UPDATE` row lock — named here as the upgrade path.)

**Rungs:** 4 (concurrency), 6 (partial failure).
**3am page saved:** the oversold inventory and the angry customer whose order you can't fill.

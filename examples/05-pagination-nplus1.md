# 5. Export orders — the query that eats the heap

**Task:** "Return all orders with their customer name for the export screen."

### Without greybeard

```csharp
public async Task<List<OrderDto>> GetOrders()
{
    var orders = await _db.Orders.ToListAsync();          // loads the entire table into memory
    return orders.Select(o => new OrderDto {
        Id = o.Id,
        Customer = _db.Customers.Find(o.CustomerId).Name  // N+1: one query PER order
    }).ToList();
}
```

Fine with 50 rows in dev. In production with 2M orders it loads the whole table
into the heap (OOM) and fires two million extra queries. The export screen takes
the database down with it.

### With greybeard

```csharp
// greybeard[5:reads]: bounded + paginated. No unbounded fan-out, no N+1.
// Projection + join happen in the DB; only the page crosses the wire.
public async Task<Page<OrderDto>> GetOrders(int page, int size, CancellationToken ct)
{
    size = Math.Clamp(size, 1, 200);  // greybeard[5:reads]: caller cannot ask for "all"

    var query = _db.Orders
        .OrderBy(o => o.Id)
        .Select(o => new OrderDto { Id = o.Id, Customer = o.Customer.Name });  // single SQL JOIN

    var total = await query.CountAsync(ct);
    var items = await query.Skip((page - 1) * size).Take(size).ToListAsync(ct);
    return new Page<OrderDto>(items, page, size, total);
    // upgrade path: keyset/seek pagination once offsets get deep (named, not built yet)
}
```

**Rung:** 5 (reads).
**3am page saved:** the out-of-memory crash when someone clicked "export everything".

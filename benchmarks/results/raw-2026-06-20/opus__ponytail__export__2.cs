// Returns each order with its customer's name, projected straight into a flat
// DTO so EF Core builds a single JOIN'd SELECT (no over-fetching, no N+1).
public async Task<List<OrderExportRow>> GetOrderExportRowsAsync(CancellationToken ct = default)
{
    return await _db.Orders
        .AsNoTracking() // read-only export, skip change tracking
        .Select(o => new OrderExportRow
        {
            OrderId      = o.Id,
            OrderDate    = o.OrderDate,
            Total        = o.Total,
            CustomerName = o.Customer.Name
        })
        .ToListAsync(ct);
}

public sealed record OrderExportRow
{
    public int OrderId { get; init; }
    public DateTime OrderDate { get; init; }
    public decimal Total { get; init; }
    public string CustomerName { get; init; } = "";
}

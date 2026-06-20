using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Read-only DTO shaped for the export screen. Only the columns the screen
/// needs are projected, which keeps the SQL narrow and the payload small.
/// </summary>
public sealed record OrderExportRow(
    int OrderId,
    string OrderNumber,
    DateTime OrderDate,
    decimal Total,
    string CustomerName);

public sealed class OrderExportService
{
    private readonly AppDbContext _db;

    public OrderExportService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns one row per order joined to its customer's name for the export
    /// screen. The query is projected directly into <see cref="OrderExportRow"/>
    /// so EF Core only SELECTs the needed columns (no full entity tracking,
    /// no N+1 lazy loads). AsNoTracking is implied by the projection but stated
    /// explicitly for clarity since this is a read-only export path.
    /// </summary>
    public async Task<IReadOnlyList<OrderExportRow>> GetOrderExportRowsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _db.Orders
            .AsNoTracking()
            // Navigation property access in the projection translates to a SQL
            // JOIN; no Include() needed because we never materialize the
            // Customer entity itself.
            .Select(o => new OrderExportRow(
                o.Id,
                o.OrderNumber,
                o.OrderDate,
                o.Total,
                o.Customer.Name))
            .OrderBy(r => r.OrderId)
            .ToListAsync(cancellationToken);
    }
}

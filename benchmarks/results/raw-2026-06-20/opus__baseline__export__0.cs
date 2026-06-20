using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Read-only projection of an order plus its owning customer's name,
/// shaped for an export/report screen.
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

    public OrderExportService(AppDbContext db) => _db = db;

    /// <summary>
    /// Returns every order together with the name of the customer that placed it.
    /// </summary>
    /// <remarks>
    /// Uses a server-side projection (Select) so EF Core only fetches the columns
    /// the export actually needs and translates the join into a single SQL query.
    /// AsNoTracking is applied because the rows are never written back.
    /// </remarks>
    public async Task<IReadOnlyList<OrderExportRow>> GetOrderExportRowsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _db.Orders
            .AsNoTracking()
            .OrderBy(o => o.OrderDate)
            .ThenBy(o => o.Id)
            .Select(o => new OrderExportRow(
                o.Id,
                o.OrderNumber,
                o.OrderDate,
                o.Total,
                o.Customer.Name)) // navigation drives an INNER JOIN in the generated SQL
            .ToListAsync(cancellationToken);
    }
}

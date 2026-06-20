using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Exports;

/// <summary>
/// Flat, read-only row shaped for the export screen: one order plus the
/// customer's display name. Using a projection DTO keeps the query lean
/// (only the columns we need are pulled from the database).
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
    /// Returns every order joined with its customer's name for the export screen.
    /// </summary>
    /// <remarks>
    /// - <c>AsNoTracking</c> because the rows are projected and never written back.
    /// - The <c>select</c> projection lets EF Core translate this into a single
    ///   SQL JOIN that only reads the five columns we actually display, instead
    ///   of materializing full Order and Customer entities.
    /// - The navigation property (<c>o.Customer.Name</c>) drives the join; if an
    ///   order can have no customer, swap to a null-conditional access and a
    ///   left join (see the commented alternative below).
    /// </remarks>
    public async Task<IReadOnlyList<OrderExportRow>> GetOrderExportRowsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.Orders
            .AsNoTracking()
            .OrderBy(o => o.OrderDate)
            .Select(o => new OrderExportRow(
                o.Id,
                o.OrderNumber,
                o.OrderDate,
                o.Total,
                o.Customer.Name))
            .ToListAsync(cancellationToken);

        return rows;
    }

    // Alternative when an order's customer is optional (nullable FK). EF Core
    // turns the null-conditional navigation access into a LEFT JOIN so orders
    // without a customer are still included, with the name coalesced to "":
    //
    // .Select(o => new OrderExportRow(
    //     o.Id,
    //     o.OrderNumber,
    //     o.OrderDate,
    //     o.Total,
    //     o.Customer != null ? o.Customer.Name : string.Empty))
}

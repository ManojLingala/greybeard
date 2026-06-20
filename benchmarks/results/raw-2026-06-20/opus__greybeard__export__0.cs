using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

// greybeard ladder for this task:
//   Rung 5 (reads a list)  -> keyset pagination, bounded page size, single projected
//                             query so the customer join does NOT become an N+1.
//   Rung 1 (money)         -> order total returned as integer minor units + currency code;
//                             no float ever crosses this boundary.
//   Rung 7 (minimum + observable) -> trust-boundary validation on paging params,
//                             AsNoTracking read, deterministic ordering.
// Rungs 2/3/4/6 do not apply: this is a read-only, in-process query. Skipped silently.

namespace Exports;

/// <summary>One row of the export screen. Money stays integer minor-units + currency.</summary>
public sealed record OrderExportRow(
    long OrderId,
    string CustomerName,
    long TotalMinorUnits,   // greybeard: integer minor units (e.g. cents), never a float currency value
    string Currency,        // greybeard: currency code travels WITH the amount, always
    DateTime CreatedUtc);

/// <summary>A bounded page of export rows plus the cursor to fetch the next page.</summary>
public sealed record OrderExportPage(
    IReadOnlyList<OrderExportRow> Rows,
    long? NextCursor);       // null => no more rows. Caller loops until null.

public static class OrderExportQueries
{
    // greybeard: hard upper bound on page size so a hostile/buggy caller can't ask for
    //            "all rows" and fan out an unbounded result set / OOM the export host.
    private const int MaxPageSize = 500;
    private const int DefaultPageSize = 100;

    /// <summary>
    /// Returns one bounded page of orders, each joined to its customer's name, for the
    /// export screen. Pages via a stable keyset cursor on OrderId (ascending).
    /// </summary>
    /// <param name="db">The EF Core context.</param>
    /// <param name="afterOrderId">
    /// Exclusive cursor: return orders with Id greater than this. Null for the first page.
    /// </param>
    /// <param name="pageSize">Requested rows per page; clamped to [1, MaxPageSize].</param>
    public static async Task<OrderExportPage> GetOrderExportPageAsync(
        AppDbContext db,
        long? afterOrderId,
        int pageSize,
        CancellationToken ct = default)
    {
        // greybeard: validate input crossing the trust boundary BEFORE it hits the DB.
        // Clamp rather than throw so an aggressive page size degrades to MaxPageSize
        // instead of failing the export; a non-positive size falls back to the default.
        if (pageSize <= 0)
        {
            pageSize = DefaultPageSize;
        }
        else if (pageSize > MaxPageSize)
        {
            pageSize = MaxPageSize;
        }

        // greybeard: cursor must reference a real, ordered key. Reject a negative cursor
        // (it can only come from a corrupted/forged token) rather than silently treating
        // it as "from the start".
        if (afterOrderId is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterOrderId), "Cursor must be non-negative.");
        }

        long cursor = afterOrderId ?? long.MinValue;

        // greybeard: ONE projected query.
        //   - Server-side join to Customer => the name comes back with each order,
        //     no per-order lazy load (kills the N+1 the veteran asks about).
        //   - AsNoTracking: read-only export, no change tracker overhead.
        //   - OrderBy(Id) + Id > cursor: keyset pagination. Stable and index-friendly,
        //     unlike Skip/Take which drifts and degrades on deep pages.
        //   - Take(pageSize + 1): one probe row tells us if a next page exists without
        //     a second COUNT round trip.
        var fetchSize = pageSize + 1;

        var fetched = await db.Orders
            .AsNoTracking()
            .Where(o => o.Id > cursor)
            .OrderBy(o => o.Id) // greybeard: deterministic order => stable, repeatable export
            .Select(o => new OrderExportRow(
                o.Id,
                // greybeard: null-safe so an order with a missing/deleted customer still
                // exports a row instead of throwing and aborting the whole batch.
                o.Customer != null ? o.Customer.Name : "(unknown)",
                o.TotalMinorUnits,
                o.Currency,
                o.CreatedUtc))
            .Take(fetchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        long? nextCursor = null;
        if (fetched.Count == fetchSize)
        {
            // We pulled the probe row => there is at least one more page.
            // Next cursor is the last row WE return (the probe is dropped).
            nextCursor = fetched[pageSize - 1].OrderId;
            fetched.RemoveAt(fetched.Count - 1);
        }

        return new OrderExportPage(fetched, nextCursor);
    }
}

// --- Minimal entity / context shape this query assumes ---------------------
// (Shown for completeness; in a real project these already exist.)

public sealed class Order
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public Customer? Customer { get; set; }

    // greybeard: money persisted as integer minor units, never decimal-as-float / double.
    public long TotalMinorUnits { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTime CreatedUtc { get; set; }
}

public sealed class Customer
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Customer> Customers => Set<Customer>();
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// greybeard rungs in play for this task:
//   Rung 5 (Reads a list)  -> pagination, bounded result set, single join (no N+1).
//   Rung 1 (Money)         -> amount stays integer minor-units + currency code; we never re-derive it as float.
//   Rung 7 (Minimum + observable) -> projection only, logged with row counts (no PII/secrets).
// Rungs 2/3/4/6 skipped: read-only single-store query, no mutation, no external call, no concurrency, no half-way failure.

namespace Exports;

// greybeard: flat projection so EF emits ONE join query, not order-then-N-customer-lookups (kills N+1).
public sealed record OrderExportRow(
    long OrderId,
    string CustomerName,
    // greybeard: money travels as integer minor-units + ISO currency code together. No decimal/double on the wire, no silent rounding.
    long TotalMinorUnits,
    string Currency,
    DateTimeOffset PlacedAtUtc);

// greybeard: keyset cursor instead of OFFSET so deep export pages don't degrade and rows don't shift under concurrent inserts.
public sealed record OrderExportPage(
    IReadOnlyList<OrderExportRow> Rows,
    long? NextCursor); // null => no more pages.

public sealed class OrderExportService
{
    private readonly AppDbContext _db;
    private readonly ILogger<OrderExportService> _log;

    // greybeard: hard ceiling on page size. Caller cannot ask for "all rows" and OOM the export box.
    private const int MaxPageSize = 500;
    private const int DefaultPageSize = 100;

    public OrderExportService(AppDbContext db, ILogger<OrderExportService> log)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Returns one page of orders with each customer's name for the export screen.
    /// </summary>
    /// <param name="afterOrderId">Keyset cursor: return orders with Id greater than this. Null/0 starts at the beginning.</param>
    /// <param name="pageSize">Requested page size; clamped to [1, MaxPageSize].</param>
    public async Task<OrderExportPage> GetExportPageAsync(
        long? afterOrderId,
        int pageSize,
        CancellationToken ct)
    {
        // greybeard: trust-boundary validation. pageSize crosses in from the caller; clamp it, never trust it.
        var size = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        var cursor = afterOrderId ?? 0;

        // greybeard: fetch size+1 to learn "is there a next page?" without a second COUNT query.
        // greybeard: AsNoTracking — export is read-only, skip the change-tracker overhead.
        // greybeard: explicit join projection; EF translates to a single SQL statement, no per-order lazy load (no N+1).
        var fetched = await _db.Orders
            .AsNoTracking()
            .Where(o => o.Id > cursor)
            .OrderBy(o => o.Id) // greybeard: stable, deterministic order — keyset paging is meaningless without it.
            .Select(o => new OrderExportRow(
                o.Id,
                // greybeard: COALESCE so a soft-deleted/missing customer doesn't drop the order or NPE the export.
                o.Customer != null ? o.Customer.Name : "(unknown customer)",
                o.TotalMinorUnits,
                o.Currency,
                o.PlacedAtUtc))
            .Take(size + 1)
            .ToListAsync(ct);

        long? next = null;
        if (fetched.Count > size)
        {
            next = fetched[size - 1].OrderId; // last row of THIS page becomes the next cursor.
            fetched.RemoveAt(size);            // drop the probe row so the caller only sees `size` rows.
        }

        // greybeard: observable — emit counts and cursor, never customer PII or amounts, into the log line.
        _log.LogInformation(
            "Order export page served. Returned={Returned} AfterCursor={Cursor} HasNext={HasNext}",
            fetched.Count, cursor, next is not null);

        return new OrderExportPage(fetched, next);
    }
}

// --- Reference entity/context shapes the query above relies on (shown for completeness) ---

public sealed class Order
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public Customer? Customer { get; set; }
    // greybeard: persisted as integer minor-units (e.g. cents) + currency code. No floating-point money column, ever.
    public long TotalMinorUnits { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset PlacedAtUtc { get; set; }
}

public sealed class Customer
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Customer> Customers => Set<Customer>();
}

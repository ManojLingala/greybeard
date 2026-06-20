// greybeard: Rung 5 (Reads a list) — pagination enforced; bounded result set; no unbounded fan-out.
// greybeard: Rung 4 (Concurrency) — read-only query; no transaction needed, but AsNoTracking prevents accidental state mutation.
// greybeard: Rung 7 — minimal correct code; observable via structured logging and cancellation support.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Greybeard.Export
{
    public sealed record OrderExportRow(
        long OrderId,
        string CustomerName,
        long AmountMinorUnits,   // greybeard: Rung 1 (Money) — integer minor-units (cents), never float.
        string CurrencyCode,     // greybeard: Rung 1 (Money) — currency code travels with the amount.
        DateTimeOffset CreatedAt
    );

    public sealed class OrderExportQuery
    {
        // greybeard: hard cap prevents a single export from reading the entire orders table into memory.
        private const int MaxPageSize = 1_000;

        private readonly AppDbContext _db;
        private readonly ILogger<OrderExportQuery> _logger;

        public OrderExportQuery(AppDbContext db, ILogger<OrderExportQuery> logger)
        {
            _db     = db     ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Returns a paginated, projection-only list of orders with customer names
        /// suitable for the export screen.
        /// </summary>
        /// <param name="pageNumber">1-based page index.</param>
        /// <param name="pageSize">Number of rows per page; clamped to [1, MaxPageSize].</param>
        /// <param name="cancellationToken">Allows the caller (e.g. HTTP request) to abort the query.</param>
        public async Task<IReadOnlyList<OrderExportRow>> GetOrderExportPageAsync(
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            // greybeard: Rung 5 — validate and clamp pagination inputs so a rogue caller
            //            cannot request pageSize=int.MaxValue and read the whole table.
            if (pageNumber < 1)
                throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page number must be >= 1.");

            // greybeard: Rung 5 — clamp silently so callers requesting huge pages still get a bounded result.
            int clampedSize = Math.Clamp(pageSize, 1, MaxPageSize);

            _logger.LogInformation(
                "OrderExport page requested. page={Page} requestedSize={RequestedSize} clampedSize={ClampedSize}",
                pageNumber, pageSize, clampedSize);

            // greybeard: Rung 4 — AsNoTracking: export is read-only; tracking would waste memory
            //            and could accidentally surface stale write-back bugs downstream.
            // greybeard: Rung 5 — server-side projection (select) so only the columns needed
            //            for the export are fetched; no N+1 because the JOIN is a single query.
            var rows = await _db.Orders
                .AsNoTracking()
                .OrderBy(o => o.Id)                       // deterministic page ordering
                .Skip((pageNumber - 1) * clampedSize)
                .Take(clampedSize)
                .Select(o => new OrderExportRow(
                    o.Id,
                    o.Customer.FullName,                  // JOIN resolved by EF; single round-trip
                    o.AmountMinorUnits,                   // greybeard: Rung 1 — integer cents stored and returned
                    o.CurrencyCode,                       // greybeard: Rung 1 — ISO-4217 code e.g. "USD"
                    o.CreatedAt
                ))
                .ToListAsync(cancellationToken);           // greybeard: Rung 7 — cancellable; honours request abort

            _logger.LogInformation(
                "OrderExport page completed. page={Page} rowsReturned={RowCount}",
                pageNumber, rows.Count);

            return rows;
        }
    }

    // ---------------------------------------------------------------------------
    // Minimal stub types so the file compiles in isolation.
    // In a real project these live in the domain / data layer.
    // ---------------------------------------------------------------------------

    public class Order
    {
        public long            Id               { get; set; }
        public Customer        Customer         { get; set; } = null!;
        public long            AmountMinorUnits { get; set; } // greybeard: Rung 1 — no decimal/double here
        public string          CurrencyCode     { get; set; } = "USD";
        public DateTimeOffset  CreatedAt        { get; set; }
    }

    public class Customer
    {
        public long   Id       { get; set; }
        public string FullName { get; set; } = string.Empty;
    }

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Order>    Orders    { get; set; } = null!;
        public DbSet<Customer> Customers { get; set; } = null!;
    }
}

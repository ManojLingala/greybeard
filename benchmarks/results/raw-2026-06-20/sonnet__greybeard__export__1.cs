// greybeard rungs that apply:
// 5. Reads a list  -> pagination + bounded result set, no unbounded fan-out
// 6. Can it fail half-way -> observable, no partial silent truncation

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Orders.Export
{
    public sealed record OrderExportRow(
        long   OrderId,
        string OrderReference,
        string CustomerName,
        string CustomerEmail,
        // greybeard: Money? -> amount in integer minor-units (e.g. cents), currency code travels with the value
        long   TotalMinorUnits,
        string CurrencyCode,
        DateTimeOffset PlacedAt,
        string Status
    );

    public sealed class OrderExportService
    {
        // greybeard: Reads a list? -> hard cap; caller must page; prevents 3am OOM on a 10M-row table
        private const int MaxPageSize = 500;

        private readonly AppDbContext _db;
        private readonly ILogger<OrderExportService> _logger;

        public OrderExportService(AppDbContext db, ILogger<OrderExportService> logger)
        {
            _db     = db     ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Returns a single page of orders joined with customer name for the export screen.
        /// </summary>
        /// <param name="pageNumber">1-based page index.</param>
        /// <param name="pageSize">Rows per page; silently clamped to [1, MaxPageSize].</param>
        /// <param name="placedAfter">Optional lower-bound filter (inclusive) on PlacedAt.</param>
        /// <param name="placedBefore">Optional upper-bound filter (exclusive) on PlacedAt.</param>
        /// <param name="ct">Cancellation token – honour it so exports can be aborted.</param>
        public async Task<ExportPage> GetOrdersForExportAsync(
            int             pageNumber  = 1,
            int             pageSize    = 100,
            DateTimeOffset? placedAfter  = null,
            DateTimeOffset? placedBefore = null,
            CancellationToken ct = default)
        {
            // greybeard: trust-boundary validation – never trust caller-supplied page params
            if (pageNumber < 1)
                throw new ArgumentOutOfRangeException(nameof(pageNumber), "Must be >= 1.");

            // greybeard: Reads a list? -> clamp page size; no unbounded result set
            pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

            int skip = (pageNumber - 1) * pageSize;

            _logger.LogInformation(
                "OrderExport: page={Page} size={Size} after={After} before={Before}",
                pageNumber, pageSize, placedAfter, placedBefore);

            // greybeard: Reads a list? -> single query with JOIN (no N+1); EF Core translates to SQL JOIN,
            //            not separate round-trips per order.
            var query = _db.Orders
                .AsNoTracking()                         // export is read-only, no change tracking overhead
                .Join(
                    _db.Customers,
                    o  => o.CustomerId,
                    c  => c.Id,
                    (o, c) => new { Order = o, Customer = c }
                );

            if (placedAfter.HasValue)
                query = query.Where(x => x.Order.PlacedAt >= placedAfter.Value);

            if (placedBefore.HasValue)
                query = query.Where(x => x.Order.PlacedAt < placedBefore.Value);

            // greybeard: Reads a list? -> deterministic ORDER BY required for stable pagination;
            //            without it, page 2 may repeat or skip rows between requests.
            query = query.OrderBy(x => x.Order.PlacedAt)
                         .ThenBy(x => x.Order.Id);

            // greybeard: Reads a list? -> count in the same filter context so total is consistent with page
            int totalCount = await query.CountAsync(ct).ConfigureAwait(false);

            List<OrderExportRow> rows = await query
                .Skip(skip)
                .Take(pageSize)
                .Select(x => new OrderExportRow(
                    x.Order.Id,
                    x.Order.Reference,
                    // greybeard: no secrets in output – CustomerName is PII but is the explicit export field;
                    //            email is included because the caller requested it for the export screen.
                    //            Do NOT include payment instrument details, raw tokens, or internal keys.
                    x.Customer.FullName,
                    x.Customer.Email,
                    // greybeard: Money? -> stored as minor-units in DB; projected as-is, no float arithmetic
                    x.Order.TotalMinorUnits,
                    x.Order.CurrencyCode,
                    x.Order.PlacedAt,
                    x.Order.Status
                ))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "OrderExport: returned {Count}/{Total} rows (page {Page})",
                rows.Count, totalCount, pageNumber);

            // greybeard: Can it fail half-way? -> if caller iterates a partial stream and cancels,
            //            they get an empty/partial ExportPage – no silent data corruption, no side effects.
            return new ExportPage(
                Rows:       rows,
                PageNumber: pageNumber,
                PageSize:   pageSize,
                TotalCount: totalCount
            );
        }
    }

    public sealed record ExportPage(
        IReadOnlyList<OrderExportRow> Rows,
        int PageNumber,
        int PageSize,
        int TotalCount
    )
    {
        public bool HasNextPage => PageNumber * PageSize < TotalCount;
    }

    // ---------------------------------------------------------------------------
    // Minimal stub entity types – replace with your actual domain models.
    // ---------------------------------------------------------------------------

    public class Order
    {
        public long           Id              { get; set; }
        public string         Reference       { get; set; } = "";
        public long           CustomerId      { get; set; }
        // greybeard: Money? -> minor-units (cents/pence/…), never decimal/float for storage
        public long           TotalMinorUnits { get; set; }
        public string         CurrencyCode    { get; set; } = "USD";
        public DateTimeOffset PlacedAt        { get; set; }
        public string         Status          { get; set; } = "";
    }

    public class Customer
    {
        public long   Id       { get; set; }
        public string FullName { get; set; } = "";
        public string Email    { get; set; } = "";
    }

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        public DbSet<Order>    Orders    { get; set; } = null!;
        public DbSet<Customer> Customers { get; set; } = null!;
    }
}

// greybeard rungs that apply:
// 5. Reads a list? -> pagination, bounded result set, no unbounded fan-out, no N+1
// 6. Can it fail half-way? -> observable, no partial silent failure

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Orders.Export
{
    public record OrderExportRow(
        long OrderId,
        string OrderNumber,
        DateTimeOffset OrderedAt,
        // greybeard: money in integer minor-units with explicit currency code; never float
        long AmountMinorUnits,
        string CurrencyCode,
        string OrderStatus,
        long CustomerId,
        string CustomerName
    );

    public class OrderExportService
    {
        private const int MaxPageSize = 500; // greybeard: hard cap — no unbounded result set
        private readonly AppDbContext _db;
        private readonly ILogger<OrderExportService> _logger;

        public OrderExportService(AppDbContext db, ILogger<OrderExportService> logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Returns a paginated, projection-only list of orders with customer names
        /// for the export screen. Caller must page through all results if totalCount > pageSize.
        /// </summary>
        /// <param name="pageNumber">1-based page number.</param>
        /// <param name="pageSize">Items per page; capped at MaxPageSize.</param>
        /// <param name="cancellationToken">Propagate caller cancellation (e.g. HTTP abort).</param>
        public async Task<OrderExportPage> GetOrdersForExportAsync(
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            // greybeard: trust-boundary validation — never trust caller-supplied sizes/offsets
            if (pageNumber < 1)
                throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page number must be >= 1.");

            // greybeard: enforce hard ceiling so a single bad call cannot exhaust memory/DB
            pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

            int skip = (pageNumber - 1) * pageSize;

            _logger.LogInformation(
                "OrderExport: fetching page {Page} pageSize {Size} skip {Skip}",
                pageNumber, pageSize, skip);

            // greybeard: single round-trip JOIN via EF projection — avoids N+1 (one query per customer)
            // AsNoTracking: read-only export path, no change tracking overhead
            var query = _db.Orders
                .AsNoTracking()
                .Join(
                    _db.Customers.AsNoTracking(),
                    order => order.CustomerId,
                    customer => customer.Id,
                    (order, customer) => new { order, customer }
                )
                .OrderBy(x => x.order.Id); // greybeard: stable sort required for consistent pagination

            // greybeard: count and data in two queries; EF cannot always do both in one pass,
            // but both are bounded and indexed. Acceptable for export screen (not hot path).
            int totalCount = await query.CountAsync(cancellationToken);

            List<OrderExportRow> rows = await query
                .Skip(skip)
                .Take(pageSize) // greybeard: bounded result set — Take is mandatory, not optional
                .Select(x => new OrderExportRow(
                    x.order.Id,
                    x.order.OrderNumber,
                    x.order.OrderedAt,
                    // greybeard: money stays in integer minor-units all the way to the caller;
                    // currency code travels with the amount so caller knows the denomination
                    x.order.AmountMinorUnits,
                    x.order.CurrencyCode,
                    x.order.Status,
                    x.customer.Id,
                    x.customer.FullName  // projection — no PII beyond what export screen needs
                ))
                .ToListAsync(cancellationToken);

            _logger.LogInformation(
                "OrderExport: returning {Count}/{Total} rows for page {Page}",
                rows.Count, totalCount, pageNumber);

            // greybeard: no secrets (card numbers, tokens, internal keys) in the projection or logs above

            return new OrderExportPage(
                Rows: rows,
                TotalCount: totalCount,
                PageNumber: pageNumber,
                PageSize: pageSize,
                HasNextPage: (skip + rows.Count) < totalCount
            );
        }
    }

    public record OrderExportPage(
        IReadOnlyList<OrderExportRow> Rows,
        int TotalCount,
        int PageNumber,
        int PageSize,
        bool HasNextPage
    );
}

// greybeard 3am review:
// - What if Orders or Customers table is missing an index on CustomerId / Id?
//   -> Add index: CREATE INDEX IX_Orders_CustomerId ON Orders(CustomerId);
//      This is outside the method but must be flagged; missing it means a full-table scan at scale.
// - What if totalCount overflows int? At 2B+ rows, cast to long. Flag for the team if approaching that.
// - Pagination by offset is O(n) scan at large offsets; for very large exports, switch to keyset
//   pagination (WHERE order.Id > lastSeenId). Flagged; acceptable for current export screen scope.
// - CancellationToken is threaded through both DB calls so HTTP client abort won't hold a DB connection.
// - No float arithmetic anywhere in this file. AmountMinorUnits is long throughout.

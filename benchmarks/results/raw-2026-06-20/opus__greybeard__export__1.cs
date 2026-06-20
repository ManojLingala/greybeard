using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// greybeard ladder for this task:
//   1. Money?       -> N/A (read-only export; no amounts mutated/computed here).
//   2. Mutation?    -> N/A (pure read).
//   3. External?    -> N/A (single DB call; DB timeout owned by connection/command config).
//   4. Concurrency? -> N/A (no writes, no lost-update window).
//   5. Reads a list? -> APPLIES. Keyset pagination, bounded result set, single projected
//                       query (no N+1, no client-side join fan-out).
//   7. Minimum correct + observable -> AsNoTracking projection, cancellation, structured logging.

namespace Exports
{
    /// <summary>Flat row for the export screen. Projected directly in SQL.</summary>
    public sealed record OrderExportRow(
        long OrderId,
        DateTimeOffset PlacedAtUtc,
        long CustomerId,
        string CustomerName);

    /// <summary>One page of export rows plus the cursor to fetch the next page.</summary>
    public sealed record OrderExportPage(
        IReadOnlyList<OrderExportRow> Rows,
        long? NextCursor); // null => no more pages

    public sealed class OrderExportService
    {
        // greybeard: rung 5 -- export screens are where "SELECT everything" OOMs the box at 3am.
        // Hard ceiling on page size regardless of what the caller asks for.
        private const int MaxPageSize = 500;
        private const int DefaultPageSize = 100;

        private readonly DbContext _db;
        private readonly ILogger<OrderExportService> _log;

        public OrderExportService(DbContext db, ILogger<OrderExportService> log)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Returns one bounded page of orders with the customer's name, ordered by a stable key.
        /// Pass the previous page's <see cref="OrderExportPage.NextCursor"/> as <paramref name="afterOrderId"/>
        /// to walk the full export; null cursor starts from the beginning.
        /// </summary>
        public async Task<OrderExportPage> GetOrderExportPageAsync(
            long? afterOrderId,
            int pageSize,
            CancellationToken ct)
        {
            // greybeard: trust-boundary validation -- clamp caller input, never echo it into a query unbounded.
            if (pageSize <= 0)
            {
                pageSize = DefaultPageSize;
            }
            pageSize = Math.Min(pageSize, MaxPageSize);

            // greybeard: rung 5 -- keyset (cursor) pagination on the PK, not OFFSET.
            // OFFSET scans+discards skipped rows; cost grows with page depth and is the classic
            // export-timeout. Keyset is O(page) at any depth and stable under concurrent inserts.
            // greybeard: explicit ORDER BY on a unique, immutable column => deterministic page boundaries
            // (no rows dropped or duplicated across pages).
            // greybeard: single projected query with an explicit join to Customer => the name is fetched
            // in the same SQL round trip. No lazy navigation = no N+1 fan-out. Server-side Select means
            // only the four needed columns cross the wire, not whole Order/Customer entities.
            // greybeard: AsNoTracking -- read-only export, skip the change tracker.
            IQueryable<OrderExportRow> query =
                from o in _db.Set<Order>().AsNoTracking()
                join c in _db.Set<Customer>().AsNoTracking() on o.CustomerId equals c.Id
                where afterOrderId == null || o.Id > afterOrderId.Value
                orderby o.Id
                select new OrderExportRow(
                    o.Id,
                    o.PlacedAtUtc,
                    c.Id,
                    // greybeard: degrade gracefully on a missing/empty name rather than emitting a blank
                    // cell that downstream CSV/Excel tooling silently misreads. Inner join already
                    // guarantees the customer row exists; this guards a NULL/empty name column.
                    c.Name == null || c.Name == "" ? "(unknown)" : c.Name);

            // greybeard: fetch pageSize + 1 to detect "is there a next page" without a second COUNT query.
            List<OrderExportRow> rows = await query
                .Take(pageSize + 1)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            long? nextCursor = null;
            if (rows.Count > pageSize)
            {
                // greybeard: the extra row is the lookahead -- drop it, hand back its predecessor's id as cursor.
                nextCursor = rows[pageSize - 1].OrderId;
                rows.RemoveAt(rows.Count - 1);
            }

            // greybeard: rung 7 -- observable. Log counts and cursor, never row contents (PII / no payload in logs).
            _log.LogInformation(
                "Order export page served: afterOrderId={AfterOrderId} pageSize={PageSize} returned={Returned} hasMore={HasMore}",
                afterOrderId,
                pageSize,
                rows.Count,
                nextCursor.HasValue);

            return new OrderExportPage(rows, nextCursor);
        }

        /// <summary>
        /// Streams the entire export as bounded pages, so callers can write rows to a file/HTTP response
        /// incrementally. Never materializes the whole table in memory.
        /// </summary>
        public async IAsyncEnumerable<OrderExportRow> StreamOrderExportAsync(
            int pageSize,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            long? cursor = null;
            do
            {
                // greybeard: cooperative cancellation between pages -- a client disconnect on a 2M-row
                // export must stop the walk, not keep hammering the DB.
                ct.ThrowIfCancellationRequested();

                OrderExportPage page = await GetOrderExportPageAsync(cursor, pageSize, ct)
                    .ConfigureAwait(false);

                foreach (OrderExportRow row in page.Rows)
                {
                    yield return row;
                }

                cursor = page.NextCursor;
            }
            while (cursor.HasValue);
        }
    }

    // --- Entity shapes assumed by the query above (yours will already exist). ---
    public class Order
    {
        public long Id { get; set; }
        public long CustomerId { get; set; }
        public DateTimeOffset PlacedAtUtc { get; set; }
    }

    public class Customer
    {
        public long Id { get; set; }
        public string? Name { get; set; }
    }
}

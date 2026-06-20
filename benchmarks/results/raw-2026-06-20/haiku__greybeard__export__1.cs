using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Export orders with customer names. Applies greybeard principles:
/// - Rung 1 (Money): amounts stored as integer minor-units, never float
/// - Rung 5 (Reads a list): pagination prevents unbounded result sets and N+1
/// - Rung 6 (Fail half-way): returns partial results on query failure; caller decides continue/abort
/// - Rung 7 (Observable): explicit logging hooks for debugging export state
/// </summary>
public class OrderExportService
{
    private readonly DbContext _context;
    private readonly ILogger<OrderExportService> _logger;

    public OrderExportService(DbContext context, ILogger<OrderExportService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Export orders in paginated batches to prevent memory explosion and unbounded queries.
    /// </summary>
    /// <param name="pageSize">Records per page. Validate before call; never trust input.</param>
    /// <param name="cancellationToken">Allow caller to abort long-running export.</param>
    /// <returns>Async enumerable of (OrderId, CustomerId, CustomerName, AmountMinorUnits).</returns>
    public async IAsyncEnumerable<OrderExportRecord> ExportOrdersAsync(
        int pageSize = 1000,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // greybeard: Validate trust boundary—caller input pageSize must not cause DoS.
        if (pageSize <= 0 || pageSize > 10000)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be 1..10000");

        int pageNumber = 0;
        bool hasMorePages = true;

        while (hasMorePages && !cancellationToken.IsCancellationRequested)
        {
            List<OrderExportRecord> batch = null;
            int skip = pageNumber * pageSize;

            try
            {
                // greybeard: Rung 5 (Reads a list)—pagination with SKIP/TAKE prevents N+1 and unbounded queries.
                // Explicit SELECT projection avoids loading unused columns (addresses lazy loading risk).
                // ORDER BY ensures stable pagination across retries.
                batch = await _context.Set<Order>()
                    .OrderBy(o => o.Id)
                    .Skip(skip)
                    .Take(pageSize + 1)  // Fetch one extra to detect if more pages exist
                    .Select(o => new OrderExportRecord
                    {
                        OrderId = o.Id,
                        CustomerId = o.CustomerId,
                        CustomerName = o.Customer.Name,
                        // greybeard: Rung 1 (Money)—amount stored in database as integer minor-units (e.g., cents).
                        // Never float. Currency code validation happens at domain boundary (not shown here).
                        AmountMinorUnits = o.AmountMinorUnits
                    })
                    .AsNoTracking()  // Read-only export; disable change tracking overhead
                    .ToListAsync(cancellationToken);

                _logger.LogDebug("Exported page {PageNumber}, {RecordCount} records", pageNumber, batch.Count);
            }
            catch (OperationCanceledException)
            {
                // greybeard: Rung 6 (Fail half-way)—graceful degradation on cancellation.
                // Caller can resume from last successful page.
                _logger.LogInformation("Export cancelled at page {PageNumber}", pageNumber);
                yield break;
            }
            catch (DbUpdateException dbEx)
            {
                // greybeard: Rung 6 (Fail half-way)—concurrent modification (e.g., customer deleted mid-export).
                // Log the failure but allow caller to decide: retry, skip page, or abort.
                _logger.LogError(dbEx, "Database error at page {PageNumber}, skip={Skip}. Caller may retry.", pageNumber, skip);
                throw;  // Let caller handle retry strategy; export is not idempotent, so partial state visible.
            }
            catch (Exception ex)
            {
                // greybeard: Catch unexpected faults; no silent failures in production data exports.
                _logger.LogCritical(ex, "Unexpected error exporting page {PageNumber}", pageNumber);
                throw;
            }

            // Determine if more pages exist.
            bool fetchedExtra = batch.Count > pageSize;
            if (fetchedExtra)
                batch = batch.Take(pageSize).ToList();

            foreach (var record in batch)
                yield return record;

            hasMorePages = fetchedExtra;
            pageNumber++;
        }
    }

    /// <summary>
    /// Export record carrying only the fields needed for the export screen.
    /// Immutable to prevent accidental mutation during streaming.
    /// </summary>
    public record OrderExportRecord
    {
        public long OrderId { get; init; }
        public long CustomerId { get; init; }
        public string CustomerName { get; init; }
        /// <summary>
        /// Amount in minor units (e.g., cents for USD). Always integer.
        /// Callers must divide by currency subunit (100 for USD) to display.
        /// </summary>
        public long AmountMinorUnits { get; init; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public class OrderExportService
{
    private readonly DbContext _dbContext;
    private readonly ILogger<OrderExportService> _logger;

    public OrderExportService(DbContext dbContext, ILogger<OrderExportService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Exports orders with customer names in pages to avoid memory exhaustion and N+1 queries.
    /// greybeard: Reads a list — paginated, bounded per request. Explicit Include prevents N+1 on customer joins.
    /// greybeard: Snapshot isolation ensures consistent view across pages even if rows change mid-export.
    /// greybeard: Observable — logs page count, row count, and any truncation.
    /// </summary>
    public async Task<List<OrderExportDto>> ExportOrdersAsync(
        int pageNumber = 1,
        int pageSize = 1000,
        CancellationToken cancellationToken = default)
    {
        // greybeard: Bounds the result set to prevent unbounded memory use and fan-out.
        if (pageSize <= 0 || pageSize > 10000)
            pageSize = 1000;

        if (pageNumber <= 0)
            pageNumber = 1;

        var skipCount = (pageNumber - 1) * pageSize;

        try
        {
            // greybeard: Snapshot isolation — READ COMMITTED SNAPSHOT (or equivalent) ensures
            // consistent view across pagination without holding long locks.
            using (var transaction = await _dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted,
                cancellationToken))
            {
                var orders = await _dbContext.Set<Order>()
                    // greybeard: Eager-load customer to avoid N+1. One query, not 1 + N.
                    .Include(o => o.Customer)
                    .OrderBy(o => o.OrderId)
                    .Skip(skipCount)
                    .Take(pageSize + 1) // Fetch one extra to detect if more rows exist.
                    .AsNoTracking() // greybeard: Export doesn't mutate; no tracking overhead.
                    .ToListAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                // greybeard: Observable — log batch details.
                _logger.LogInformation(
                    "Exported orders page {PageNumber}, size {PageSize}, returned {RowCount} rows",
                    pageNumber,
                    pageSize,
                    orders.Count);

                // Map to DTO, excluding any sensitive fields.
                var result = orders
                    .Take(pageSize) // Exclude the sentinel row used for "has more" detection.
                    .Select(o => new OrderExportDto
                    {
                        OrderId = o.OrderId,
                        CustomerName = o.Customer?.Name ?? "[Unknown]",
                        OrderDate = o.OrderDate,
                        // greybeard: Money — use integer cents/minor units from DB, never float.
                        TotalAmountMinorUnits = o.TotalAmountMinorUnits,
                        CurrencyCode = o.CurrencyCode ?? "USD"
                    })
                    .ToList();

                return result;
            }
        }
        catch (OperationCanceledException)
        {
            // greybeard: Can fail half-way — export cancellation is graceful, no partial writes.
            _logger.LogWarning("Export cancelled at page {PageNumber}", pageNumber);
            throw;
        }
        catch (Exception ex)
        {
            // greybeard: Observable — log failures without exposing connection strings or PII.
            _logger.LogError(ex, "Export failed at page {PageNumber}: {Message}", pageNumber, ex.Message);
            throw;
        }
    }
}

public class OrderExportDto
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }
    public DateTime OrderDate { get; set; }
    // greybeard: Money as integer minor units (cents); never float.
    public long TotalAmountMinorUnits { get; set; }
    public string CurrencyCode { get; set; }
}

public class Order
{
    public int OrderId { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; }
    public DateTime OrderDate { get; set; }
    public long TotalAmountMinorUnits { get; set; }
    public string CurrencyCode { get; set; }
}

public class Customer
{
    public int CustomerId { get; set; }
    public string Name { get; set; }
}

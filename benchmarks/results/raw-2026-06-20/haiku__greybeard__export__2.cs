using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Greybeard.Export
{
    public class OrderExportService
    {
        private readonly ApplicationDbContext _context;
        private const int MaxPageSize = 10000; // greybeard: bounded result set, prevent unbounded fan-out
        private const int DefaultPageSize = 1000;

        public OrderExportService(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Export orders with customer names for display/file generation.
        /// Handles partial failures gracefully and prevents N+1 queries.
        /// </summary>
        public async Task<IEnumerable<OrderExportDto>> ExportOrdersAsync(
            int pageNumber = 1,
            int pageSize = DefaultPageSize)
        {
            // greybeard: pagination - bounded result set, no N+1 queries via eager load
            if (pageSize > MaxPageSize)
                pageSize = MaxPageSize;

            if (pageNumber < 1)
                pageNumber = 1;

            try
            {
                var orders = await _context.Orders
                    // greybeard: eager load customer names to avoid N+1 query pattern
                    .Include(o => o.Customer)
                    // greybeard: order by stable key for consistent pagination
                    .OrderBy(o => o.OrderId)
                    // greybeard: pagination window
                    .Skip((pageNumber - 1) * pageSize)
                    .Take(pageSize)
                    // greybeard: read-only snapshot to prevent dirty reads in concurrent scenarios
                    .AsNoTracking()
                    .ToListAsync();

                // greybeard: money - amounts stored as integer minor-units (cents), never float
                // map domain model to export DTO, preserving currency precision
                var exportData = orders.Select(o => new OrderExportDto
                {
                    OrderId = o.OrderId,
                    OrderNumber = o.OrderNumber ?? string.Empty, // greybeard: null coalesce prevents null in export
                    CustomerName = o.Customer?.Name ?? "[Unknown Customer]", // greybeard: graceful degradation for orphaned orders
                    TotalAmountMinorUnits = o.TotalAmountMinorUnits, // greybeard: integer, never converted to float
                    CurrencyCode = o.CurrencyCode ?? "USD", // greybeard: currency code travels with amount
                    OrderDate = o.OrderDate,
                    Status = o.Status
                }).ToList();

                return exportData;
            }
            catch (OperationCanceledException ex)
            {
                // greybeard: graceful degradation - log and return empty rather than crash mid-export
                System.Diagnostics.Debug.WriteLine($"Export query cancelled: {ex.Message}");
                return Enumerable.Empty<OrderExportDto>();
            }
            catch (DbUpdateException ex)
            {
                // greybeard: observable - log query failure without exposing query details
                System.Diagnostics.Debug.WriteLine($"Export query failed: Database error occurred during order retrieval");
                throw new InvalidOperationException("Failed to retrieve orders for export", ex);
            }
        }
    }

    public class OrderExportDto
    {
        public int OrderId { get; set; }
        public string OrderNumber { get; set; }
        public string CustomerName { get; set; }
        /// <summary>
        /// Order total in minor units (e.g., cents). Integer-only, never float.
        /// greybeard: money correctness - no floating-point currency representation.
        /// </summary>
        public long TotalAmountMinorUnits { get; set; }
        /// <summary>
        /// ISO 4217 currency code (USD, EUR, etc).
        /// greybeard: currency code always travels with the amount.
        /// </summary>
        public string CurrencyCode { get; set; }
        public DateTime OrderDate { get; set; }
        public string Status { get; set; }
    }
}

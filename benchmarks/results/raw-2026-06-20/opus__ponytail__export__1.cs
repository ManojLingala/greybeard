using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

// Minimal projection for the export screen: only the columns we actually need.
public record OrderExportRow(int OrderId, string CustomerName, decimal Total);

public class OrderExportService
{
    private readonly AppDbContext _db;

    public OrderExportService(AppDbContext db) => _db = db;

    // Returns each order with its customer's name, shaped for export.
    // Relies on the navigation property + Select so EF Core emits a single
    // JOIN and pulls only the needed columns. No tracking needed for a read-only export.
    public Task<List<OrderExportRow>> GetExportRowsAsync()
    {
        return _db.Orders
            .AsNoTracking()
            .Select(o => new OrderExportRow(o.Id, o.Customer.Name, o.Total))
            .ToListAsync();
    }
}

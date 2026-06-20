using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

// Minimal projection: pull only the columns the export screen needs.
// A flat row (OrderId, OrderDate, Total, CustomerName) is all that's required,
// so we project straight into it and let EF translate it to a single SQL JOIN.
public record OrderExportRow(int OrderId, DateTime OrderDate, decimal Total, string CustomerName);

public static class OrderExportQueries
{
    public static Task<List<OrderExportRow>> GetOrderExportRowsAsync(AppDbContext db)
    {
        return db.Orders
            .AsNoTracking() // read-only export, no change tracking needed
            .Select(o => new OrderExportRow(
                o.Id,
                o.OrderDate,
                o.Total,
                o.Customer.Name)) // navigation property -> EF emits the JOIN for us
            .ToListAsync();
    }
}

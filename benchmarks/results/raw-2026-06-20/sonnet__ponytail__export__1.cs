using Microsoft.EntityFrameworkCore;

public record OrderExportRow(int OrderId, DateTime OrderDate, decimal Total, string CustomerName);

public async Task<List<OrderExportRow>> GetOrdersForExportAsync(AppDbContext db)
{
    return await db.Orders
        .Select(o => new OrderExportRow(
            o.Id,
            o.OrderDate,
            o.Total,
            o.Customer.Name))
        .ToListAsync();
}

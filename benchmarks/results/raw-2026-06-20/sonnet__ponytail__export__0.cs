using Microsoft.EntityFrameworkCore;

public class OrderExportDto
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public decimal Total { get; set; }
}

public static class OrderExportQuery
{
    public static async Task<List<OrderExportDto>> GetOrdersForExportAsync(AppDbContext db, CancellationToken ct = default)
    {
        return await db.Orders
            .AsNoTracking()
            .Select(o => new OrderExportDto
            {
                OrderId = o.Id,
                CustomerName = o.Customer.Name,
                OrderDate = o.OrderDate,
                Total = o.Total
            })
            .ToListAsync(ct);
    }
}

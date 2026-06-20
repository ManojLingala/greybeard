using Microsoft.EntityFrameworkCore;

public class OrderExportDto
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public decimal Total { get; set; }
}

public async Task<List<OrderExportDto>> GetOrdersForExportAsync(DbContext db)
{
    return await db.Set<Order>()
        .Select(o => new OrderExportDto
        {
            OrderId = o.Id,
            CustomerName = o.Customer.Name,
            OrderDate = o.OrderDate,
            Total = o.Total
        })
        .AsNoTracking()
        .ToListAsync();
}

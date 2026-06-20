using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class OrderExportService
{
    private readonly DbContext _context;

    public OrderExportService(DbContext context)
    {
        _context = context;
    }

    public async Task<List<OrderExportDto>> GetOrdersForExport()
    {
        return await _context.Set<Order>()
            .Include(o => o.Customer)
            .Select(o => new OrderExportDto
            {
                OrderId = o.OrderId,
                CustomerName = o.Customer.Name,
                OrderDate = o.OrderDate,
                Total = o.Total
            })
            .ToListAsync();
    }
}

public class OrderExportDto
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }
    public DateTime OrderDate { get; set; }
    public decimal Total { get; set; }
}

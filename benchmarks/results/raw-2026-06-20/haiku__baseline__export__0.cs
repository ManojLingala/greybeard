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
        return await _context.Orders
            .Include(o => o.Customer)
            .Select(o => new OrderExportDto
            {
                OrderId = o.OrderId,
                OrderDate = o.OrderDate,
                CustomerName = o.Customer.Name,
                TotalAmount = o.TotalAmount,
                Status = o.Status
            })
            .ToListAsync();
    }
}

public class OrderExportDto
{
    public int OrderId { get; set; }
    public DateTime OrderDate { get; set; }
    public string CustomerName { get; set; }
    public decimal TotalAmount { get; set; }
    public string Status { get; set; }
}

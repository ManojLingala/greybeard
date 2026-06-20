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

    public async Task<List<OrderExportDto>> GetOrdersForExportAsync()
    {
        return await _context.Set<Order>()
            .Include(o => o.Customer)
            .Select(o => new OrderExportDto
            {
                OrderId = o.Id,
                OrderDate = o.OrderDate,
                CustomerName = o.Customer.Name,
                Amount = o.Amount
            })
            .ToListAsync();
    }
}

public class OrderExportDto
{
    public int OrderId { get; set; }
    public DateTime OrderDate { get; set; }
    public string CustomerName { get; set; }
    public decimal Amount { get; set; }
}

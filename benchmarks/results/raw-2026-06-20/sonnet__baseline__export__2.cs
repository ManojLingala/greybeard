using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public class OrderExportDto
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }
    public DateTime OrderDate { get; set; }
    public decimal TotalAmount { get; set; }
    public string Status { get; set; }
}

public class OrderExportService
{
    private readonly AppDbContext _dbContext;

    public OrderExportService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<OrderExportDto>> GetOrdersWithCustomerNamesAsync()
    {
        return await _dbContext.Orders
            .Include(o => o.Customer)
            .Select(o => new OrderExportDto
            {
                OrderId = o.Id,
                CustomerName = o.Customer.Name,
                OrderDate = o.OrderDate,
                TotalAmount = o.TotalAmount,
                Status = o.Status
            })
            .OrderBy(o => o.OrderDate)
            .ToListAsync();
    }
}

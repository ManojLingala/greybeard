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

    /// <summary>
    /// Returns orders together with each customer's name for export.
    /// </summary>
    public async Task<List<OrderExportDto>> GetOrdersForExportAsync()
    {
        return await _context.Set<Order>()
            .AsNoTracking()
            .Include(o => o.Customer)
            .Select(o => new OrderExportDto
            {
                OrderId = o.Id,
                CustomerName = o.Customer.Name,
                OrderDate = o.OrderDate,
                TotalAmount = o.TotalAmount,
                Status = o.Status
            })
            .ToListAsync();
    }
}

public class OrderExportDto
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }
    public DateTime OrderDate { get; set; }
    public decimal TotalAmount { get; set; }
    public string Status { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public DateTime OrderDate { get; set; }
    public decimal TotalAmount { get; set; }
    public string Status { get; set; }

    public virtual Customer Customer { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; }

    public virtual ICollection<Order> Orders { get; set; }
}

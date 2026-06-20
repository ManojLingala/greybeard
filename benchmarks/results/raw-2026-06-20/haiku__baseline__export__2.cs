using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public class OrderExportService
{
    private readonly ApplicationDbContext _context;

    public OrderExportService(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Returns orders together with each customer's name for export screen.
    /// </summary>
    public async Task<List<OrderExportDto>> GetOrdersForExportAsync()
    {
        return await _context.Orders
            .Include(o => o.Customer)
            .Select(o => new OrderExportDto
            {
                OrderId = o.OrderId,
                OrderDate = o.OrderDate,
                TotalAmount = o.TotalAmount,
                CustomerName = o.Customer.Name,
                CustomerEmail = o.Customer.Email,
                Status = o.Status
            })
            .ToListAsync();
    }
}

public class OrderExportDto
{
    public int OrderId { get; set; }
    public DateTime OrderDate { get; set; }
    public decimal TotalAmount { get; set; }
    public string CustomerName { get; set; }
    public string CustomerEmail { get; set; }
    public string Status { get; set; }
}

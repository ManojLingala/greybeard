using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Stock { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderItem> Items { get; set; } = new List<OrderItem>();
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; }
    public int Quantity { get; set; }
}

public class AppDbContext : DbContext
{
    public DbSet<Product> Products { get; set; }
    public DbSet<Order> Orders { get; set; }
    public DbSet<OrderItem> OrderItems { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
}

public class OrderService
{
    private readonly AppDbContext _dbContext;

    public OrderService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Places an order and decrements product stock for each item in the order.
    /// Throws InvalidOperationException if any product has insufficient stock.
    /// </summary>
    public async Task<Order> PlaceOrderAsync(List<OrderItem> items)
    {
        // Begin a transaction to ensure atomicity
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();

        try
        {
            // Load all required products in one query
            var productIds = items.ConvertAll(i => i.ProductId);
            var products = await _dbContext.Products
                .Where(p => productIds.Contains(p.Id))
                .ToListAsync();

            // Validate and decrement stock
            foreach (var item in items)
            {
                var product = products.Find(p => p.Id == item.ProductId);

                if (product == null)
                    throw new InvalidOperationException($"Product with ID {item.ProductId} was not found.");

                if (product.Stock < item.Quantity)
                    throw new InvalidOperationException(
                        $"Insufficient stock for product '{product.Name}'. " +
                        $"Requested: {item.Quantity}, Available: {product.Stock}.");

                product.Stock -= item.Quantity;
            }

            // Create the order
            var order = new Order
            {
                CreatedAt = DateTime.UtcNow,
                Items = items
            };

            _dbContext.Orders.Add(order);
            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            return order;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}

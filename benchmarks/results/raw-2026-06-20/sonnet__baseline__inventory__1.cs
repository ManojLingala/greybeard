using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public class OrderService
{
    private readonly AppDbContext _context;

    public OrderService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Places an order and decrements the product stock accordingly.
    /// Throws an exception if there is insufficient stock.
    /// </summary>
    public async Task PlaceOrderAsync(int productId, int quantity)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            // Retrieve the product with a pessimistic lock to prevent race conditions
            var product = await _context.Products
                .FromSqlRaw("SELECT * FROM Products WITH (UPDLOCK) WHERE Id = {0}", productId)
                .FirstOrDefaultAsync();

            if (product == null)
                throw new InvalidOperationException($"Product with ID {productId} not found.");

            if (product.Stock < quantity)
                throw new InvalidOperationException(
                    $"Insufficient stock for product '{product.Name}'. Available: {product.Stock}, Requested: {quantity}.");

            // Decrement stock
            product.Stock -= quantity;

            // Create the order record
            var order = new Order
            {
                ProductId = productId,
                Quantity = quantity,
                OrderDate = DateTime.UtcNow
            };

            _context.Orders.Add(order);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}

// Example entity definitions for reference:

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Stock { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public DateTime OrderDate { get; set; }
    public Product Product { get; set; }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product> Products { get; set; }
    public DbSet<Order> Orders { get; set; }
}

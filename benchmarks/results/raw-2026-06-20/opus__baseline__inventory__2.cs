```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Shop.Ordering;

public class OrderLine
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Stock { get; set; }
}

public class InsufficientStockException : Exception
{
    public int ProductId { get; }
    public int Requested { get; }
    public int Available { get; }

    public InsufficientStockException(int productId, int requested, int available)
        : base($"Insufficient stock for product {productId}: requested {requested}, available {available}.")
    {
        ProductId = productId;
        Requested = requested;
        Available = available;
    }
}

public class OrderService
{
    private readonly AppDbContext _db;

    public OrderService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Decrements product stock for each line of a placed order.
    /// Runs in a transaction so all decrements succeed or none do, and validates
    /// that sufficient stock exists before committing.
    /// </summary>
    public async Task DecrementStockAsync(
        IEnumerable<OrderLine> orderLines,
        CancellationToken cancellationToken = default)
    {
        // Aggregate quantities per product so duplicate lines are handled correctly.
        var requested = orderLines
            .GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

        if (requested.Count == 0)
        {
            return;
        }

        if (requested.Values.Any(q => q <= 0))
        {
            throw new ArgumentException("Order line quantities must be positive.", nameof(orderLines));
        }

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken);

        var productIds = requested.Keys.ToList();

        var products = await _db.Products
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        foreach (var (productId, quantity) in requested)
        {
            var product = products.FirstOrDefault(p => p.Id == productId);
            if (product is null)
            {
                throw new InvalidOperationException($"Product {productId} was not found.");
            }

            if (product.Stock < quantity)
            {
                throw new InsufficientStockException(productId, quantity, product.Stock);
            }

            product.Stock -= quantity;
        }

        // SaveChanges will throw DbUpdateConcurrencyException if a concurrency token
        // (e.g. rowversion) on Product is configured and another transaction won the race.
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
}
```

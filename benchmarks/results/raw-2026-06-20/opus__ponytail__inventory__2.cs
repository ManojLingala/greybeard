using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

// Decrements product stock when an order is placed.
//
// Laziest-correct approach:
// - One DbContext, one transaction (EF Core wraps SaveChanges in a transaction already).
// - Optimistic concurrency on Product.Stock via a [Timestamp]/rowversion column means
//   concurrent decrements won't oversell; a conflicting save throws and the caller can retry.
// - Guard against going negative explicitly so we return a clear domain error, not a DB constraint.
public sealed class InventoryService
{
    private readonly AppDbContext _db;

    public InventoryService(AppDbContext db) => _db = db;

    /// <summary>
    /// Reserves <paramref name="quantity"/> units of <paramref name="productId"/>.
    /// Returns false if the product doesn't exist or has insufficient stock.
    /// </summary>
    public async Task<bool> PlaceOrderAsync(int productId, int quantity, CancellationToken ct = default)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");

        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null || product.Stock < quantity)
            return false;

        product.Stock -= quantity;

        // EF Core tracks the change; SaveChanges issues the UPDATE inside its own transaction.
        // With a rowversion concurrency token, a racing decrement throws DbUpdateConcurrencyException.
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

// Minimal supporting types (your real model likely already has these).
public class Product
{
    public int Id { get; set; }
    public int Stock { get; set; }

    // Optimistic concurrency token prevents two orders from overselling the same units.
    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Shop.Inventory;

/// <summary>
/// Represents a single line of an order: which product and how many units.
/// </summary>
public sealed record OrderLine(int ProductId, int Quantity);

/// <summary>
/// Thrown when an order cannot be fulfilled because one or more products
/// do not have enough stock on hand.
/// </summary>
public sealed class InsufficientStockException : Exception
{
    public InsufficientStockException(int productId, int requested, int available)
        : base($"Insufficient stock for product {productId}: requested {requested}, available {available}.")
    {
        ProductId = productId;
        Requested = requested;
        Available = available;
    }

    public int ProductId { get; }
    public int Requested { get; }
    public int Available { get; }
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int StockOnHand { get; set; }

    /// <summary>
    /// Concurrency token. EF Core uses this to detect competing updates so two
    /// simultaneous orders cannot both decrement from the same starting value.
    /// </summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public sealed class ShopDbContext : DbContext
{
    public ShopDbContext(DbContextOptions<ShopDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Name).IsRequired();
            b.Property(p => p.StockOnHand).IsRequired();
            b.Property(p => p.RowVersion).IsRowVersion();
        });
    }
}

public sealed class StockService
{
    private readonly ShopDbContext _db;

    public StockService(ShopDbContext db) => _db = db;

    /// <summary>
    /// Atomically decrements stock for every line of an order.
    ///
    /// The whole operation runs inside a single transaction: either every line
    /// is decremented or none are. A row-version concurrency token guards against
    /// two orders racing on the same product, and an optimistic-concurrency retry
    /// loop re-reads and re-validates if a conflict is detected.
    /// </summary>
    /// <exception cref="ArgumentException">An order line has a non-positive quantity, or there are no lines.</exception>
    /// <exception cref="KeyNotFoundException">A referenced product does not exist.</exception>
    /// <exception cref="InsufficientStockException">A product does not have enough stock.</exception>
    public async Task DecrementStockForOrderAsync(
        IReadOnlyCollection<OrderLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
            throw new ArgumentException("An order must contain at least one line.", nameof(lines));

        // Collapse duplicate product lines so each product is touched exactly once.
        var demand = lines
            .GroupBy(l => l.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                Quantity = g.Sum(l => l.Quantity),
            })
            .ToList();

        foreach (var d in demand)
        {
            if (d.Quantity <= 0)
                throw new ArgumentException(
                    $"Quantity for product {d.ProductId} must be positive.", nameof(lines));
        }

        var productIds = demand.Select(d => d.ProductId).ToList();

        const int maxRetries = 3;
        for (var attempt = 0; ; attempt++)
        {
            await using var transaction =
                await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var products = await _db.Products
                    .Where(p => productIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var d in demand)
                {
                    if (!products.TryGetValue(d.ProductId, out var product))
                        throw new KeyNotFoundException($"Product {d.ProductId} was not found.");

                    if (product.StockOnHand < d.Quantity)
                        throw new InsufficientStockException(
                            d.ProductId, d.Quantity, product.StockOnHand);

                    product.StockOnHand -= d.Quantity;
                }

                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxRetries)
            {
                // Another order updated one of these products first. Roll back,
                // drop the now-stale tracked entities, and retry with fresh data.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                foreach (var entry in _db.ChangeTracker.Entries<Product>().ToList())
                    entry.State = EntityState.Detached;
            }
        }
    }
}

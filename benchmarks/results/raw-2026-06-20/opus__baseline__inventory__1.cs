using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Shop.Inventory
{
    // Domain entities
    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        // Quantity currently available to sell.
        public int StockQuantity { get; set; }

        // Optional concurrency token. When mapped as a row version (e.g.
        // .IsRowVersion() in OnModelCreating), EF Core will throw a
        // DbUpdateConcurrencyException if two transactions race to decrement
        // the same product, which prevents overselling.
        public byte[]? RowVersion { get; set; }
    }

    public class OrderLine
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }

    public class ShopDbContext : DbContext
    {
        public ShopDbContext(DbContextOptions<ShopDbContext> options)
            : base(options)
        {
        }

        public DbSet<Product> Products => Set<Product>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>(entity =>
            {
                entity.HasKey(p => p.Id);
                entity.Property(p => p.RowVersion).IsRowVersion();
            });
        }
    }

    /// <summary>
    /// Thrown when an order cannot be fulfilled because one or more products
    /// do not have enough stock on hand.
    /// </summary>
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

    public class InventoryService
    {
        private readonly ShopDbContext _db;

        public InventoryService(ShopDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Decrements stock for every line in an order atomically.
        /// Either all lines succeed or the whole operation is rolled back.
        /// </summary>
        /// <param name="lines">The order lines (product id + quantity).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <exception cref="ArgumentNullException">If <paramref name="lines"/> is null.</exception>
        /// <exception cref="ArgumentException">If any line has a non-positive quantity.</exception>
        /// <exception cref="KeyNotFoundException">If a referenced product does not exist.</exception>
        /// <exception cref="InsufficientStockException">If a product lacks sufficient stock.</exception>
        /// <exception cref="DbUpdateConcurrencyException">If a concurrent update wins the race.</exception>
        public async Task DecrementStockForOrderAsync(
            IEnumerable<OrderLine> lines,
            CancellationToken cancellationToken = default)
        {
            if (lines is null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            // Collapse duplicate product references into a single quantity so a
            // product appearing twice in one order is handled correctly.
            var requested = lines
                .GroupBy(l => l.ProductId)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

            if (requested.Count == 0)
            {
                return;
            }

            foreach (var (productId, quantity) in requested)
            {
                if (quantity <= 0)
                {
                    throw new ArgumentException(
                        $"Order line quantity for product {productId} must be positive.",
                        nameof(lines));
                }
            }

            var productIds = requested.Keys.ToList();

            // Wrap in an explicit transaction so the read-check-write across
            // multiple products is all-or-nothing.
            await using var transaction =
                await _db.Database.BeginTransactionAsync(cancellationToken);

            // Load all required products in a single query and track them so
            // changes are persisted on SaveChanges.
            var products = await _db.Products
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, cancellationToken);

            foreach (var (productId, quantity) in requested)
            {
                if (!products.TryGetValue(productId, out var product))
                {
                    throw new KeyNotFoundException($"Product {productId} was not found.");
                }

                if (product.StockQuantity < quantity)
                {
                    throw new InsufficientStockException(
                        productId, quantity, product.StockQuantity);
                }

                product.StockQuantity -= quantity;
            }

            // RowVersion concurrency tokens cause SaveChanges to fail if another
            // transaction modified the same product concurrently, so we never
            // oversell under load.
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }
}

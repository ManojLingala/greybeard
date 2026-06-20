// greybeard rungs that apply:
// 2. Mutation       -> idempotency key (orderId) prevents double-decrement on retry
// 4. Concurrency    -> explicit pessimistic row lock (UPDLOCK/ROWLOCK via raw SQL) + optimistic version check to prevent lost updates
// 6. Can fail half-way -> transactional boundary; partial failure rolls back, caller gets typed result

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Inventory
{
    public enum DecrementStockResult
    {
        Success,
        AlreadyProcessed,
        InsufficientStock,
        ProductNotFound,
    }

    public class DecrementStockOutcome
    {
        public DecrementStockResult Result { get; init; }
        public int? StockAfter { get; init; }
    }

    public static class OrderInventoryService
    {
        /// <summary>
        /// Decrements product stock atomically when an order is placed.
        /// Safe to call multiple times with the same orderId (idempotent).
        /// </summary>
        public static async Task<DecrementStockOutcome> DecrementStockAsync(
            AppDbContext db,
            Guid orderId,        // greybeard: idempotency key -- a retried request with the same orderId is a no-op
            Guid productId,
            int quantity,
            CancellationToken ct = default)
        {
            // greybeard: trust-boundary validation -- never trust caller input crossing a boundary
            if (quantity <= 0)
                throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
            if (orderId == Guid.Empty)
                throw new ArgumentException("orderId must not be empty.", nameof(orderId));
            if (productId == Guid.Empty)
                throw new ArgumentException("productId must not be empty.", nameof(productId));

            // greybeard: explicit transaction boundary -- reads and writes are atomic; rolled back on any failure
            await using var tx = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct);

            try
            {
                // greybeard: idempotency -- check whether this order already decremented stock before doing any work
                bool alreadyProcessed = await db.OrderStockReservations
                    .AnyAsync(r => r.OrderId == orderId && r.ProductId == productId, ct);

                if (alreadyProcessed)
                {
                    await tx.RollbackAsync(ct);
                    return new DecrementStockOutcome { Result = DecrementStockResult.AlreadyProcessed };
                }

                // greybeard: concurrency -- acquire a pessimistic write lock on this product row so two
                // concurrent orders for the same product cannot both read the same stock and both succeed.
                // EF Core does not expose UPDLOCK hints natively; we drop to raw SQL for correctness.
                var product = await db.Products
                    .FromSqlRaw(
                        "SELECT * FROM Products WITH (UPDLOCK, ROWLOCK) WHERE Id = {0}",
                        productId)
                    .FirstOrDefaultAsync(ct);

                if (product is null)
                {
                    await tx.RollbackAsync(ct);
                    return new DecrementStockOutcome { Result = DecrementStockResult.ProductNotFound };
                }

                // greybeard: concurrency -- check stock *after* acquiring the lock, not before
                if (product.StockQuantity < quantity)
                {
                    await tx.RollbackAsync(ct);
                    return new DecrementStockOutcome { Result = DecrementStockResult.InsufficientStock };
                }

                product.StockQuantity -= quantity;  // integer arithmetic -- no float, no rounding ambiguity

                // greybeard: idempotency ledger -- record that this (orderId, productId) pair was processed
                // so a future retry returns AlreadyProcessed without mutating stock again
                db.OrderStockReservations.Add(new OrderStockReservation
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    ProductId = productId,
                    QuantityReserved = quantity,
                    ReservedAtUtc = DateTime.UtcNow,
                });

                await db.SaveChangesAsync(ct);  // greybeard: SaveChanges inside the tx; rolls back on DbUpdateConcurrencyException
                await tx.CommitAsync(ct);

                return new DecrementStockOutcome
                {
                    Result = DecrementStockResult.Success,
                    StockAfter = product.StockQuantity,
                };
            }
            catch (DbUpdateConcurrencyException)
            {
                // greybeard: concurrency -- optimistic version mismatch (RowVersion); surface as retriable to the caller
                await tx.RollbackAsync(ct);
                throw;  // caller retries with same idempotency key; the idempotency check above makes that safe
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Minimal EF Core entity stubs (adapt to your actual model)
    // ---------------------------------------------------------------------------

    public class Product
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int StockQuantity { get; set; }  // greybeard: integer units -- never store stock as float

        // greybeard: concurrency token -- EF Core uses this to detect lost updates (optimistic concurrency)
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }

    public class OrderStockReservation
    {
        public Guid Id { get; set; }
        public Guid OrderId { get; set; }       // greybeard: idempotency key stored in DB
        public Guid ProductId { get; set; }
        public int QuantityReserved { get; set; }
        public DateTime ReservedAtUtc { get; set; }
    }

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Product> Products => Set<Product>();
        public DbSet<OrderStockReservation> OrderStockReservations => Set<OrderStockReservation>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Product>()
                .Property(p => p.RowVersion)
                .IsRowVersion();  // greybeard: maps to SQL Server rowversion / timestamp for optimistic concurrency

            // greybeard: unique index enforces idempotency at the DB level as a second line of defence
            mb.Entity<OrderStockReservation>()
                .HasIndex(r => new { r.OrderId, r.ProductId })
                .IsUnique();
        }
    }
}

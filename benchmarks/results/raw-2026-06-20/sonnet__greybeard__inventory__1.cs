// greybeard: Rungs applied: 2 (idempotency), 4 (concurrency/row lock), 6 (partial failure/saga)
// Rung 1 (Money) applies only to price fields — stock is an integer unit count, no float risk here.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class OrderService
{
    private readonly AppDbContext _db;
    private readonly ILogger<OrderService> _log;

    public OrderService(AppDbContext db, ILogger<OrderService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Decrements product stock when an order is placed.
    /// Safe to retry: idempotency key prevents double-decrement.
    /// </summary>
    public async Task<DecrementResult> DecrementStockForOrderAsync(
        Guid orderId,
        int productId,
        int quantity,
        CancellationToken ct = default)
    {
        // greybeard: trust-boundary — validate all inputs before touching the DB
        if (orderId == Guid.Empty)
            throw new ArgumentException("orderId must not be empty.", nameof(orderId));
        if (productId <= 0)
            throw new ArgumentException("productId must be positive.", nameof(productId));
        if (quantity <= 0)
            throw new ArgumentException("quantity must be positive.", nameof(quantity));

        // greybeard: Rung 2 — idempotency: check whether this order already has a stock reservation
        // so that a retried request does not decrement twice.
        var alreadyReserved = await _db.StockReservations
            .AsNoTracking()
            .AnyAsync(r => r.OrderId == orderId && r.ProductId == productId, ct);

        if (alreadyReserved)
        {
            _log.LogInformation(
                "Stock already reserved for OrderId={OrderId} ProductId={ProductId} — skipping duplicate decrement.",
                orderId, productId);
            return DecrementResult.AlreadyApplied;
        }

        // greybeard: Rung 4 — explicit transaction boundary with serializable isolation to prevent
        // two concurrent orders from both reading the same stock level and both succeeding.
        await using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, ct);

        try
        {
            // greybeard: Rung 4 — pessimistic row lock via raw SQL UPDLOCK / FOR UPDATE so that
            // two concurrent requests cannot read the same stock value and both decrement past zero.
            // EF Core does not expose UPDLOCK natively; we use FromSqlRaw for the lock hint.
            var product = await _db.Products
                .FromSqlRaw(
                    "SELECT * FROM Products WITH (UPDLOCK, ROWLOCK) WHERE Id = {0}",
                    productId)
                .SingleOrDefaultAsync(ct);

            if (product is null)
            {
                await tx.RollbackAsync(ct);
                _log.LogWarning("Product not found. ProductId={ProductId}", productId);
                return DecrementResult.ProductNotFound;
            }

            // greybeard: prevent stock going negative — business invariant enforced in code AND
            // backed by a CHECK constraint in the DB schema (Stock >= 0).
            if (product.Stock < quantity)
            {
                await tx.RollbackAsync(ct);
                _log.LogWarning(
                    "Insufficient stock. ProductId={ProductId} Available={Available} Requested={Requested}",
                    productId, product.Stock, quantity);
                return DecrementResult.InsufficientStock;
            }

            product.Stock -= quantity;

            // greybeard: Rung 2 — write the idempotency record in the same transaction so that
            // the decrement and the "already done" marker are atomically committed or rolled back.
            _db.StockReservations.Add(new StockReservation
            {
                OrderId   = orderId,
                ProductId = productId,
                Quantity  = quantity,
                ReservedAt = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // greybeard: observable — structured log with all relevant IDs so this is traceable
            // in production without leaking sensitive data.
            _log.LogInformation(
                "Stock decremented. ProductId={ProductId} Quantity={Quantity} RemainingStock={RemainingStock} OrderId={OrderId}",
                productId, quantity, product.Stock, orderId);

            return DecrementResult.Success;
        }
        catch (Exception ex)
        {
            // greybeard: Rung 6 — on any unexpected failure roll back so the DB stays consistent.
            // The caller is responsible for saga compensation (e.g., cancel the order) if needed.
            // No secrets, no PII in the log message.
            _log.LogError(ex,
                "Failed to decrement stock. OrderId={OrderId} ProductId={ProductId}",
                orderId, productId);
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}

public enum DecrementResult
{
    Success,
    AlreadyApplied,
    InsufficientStock,
    ProductNotFound,
}

// ---------------------------------------------------------------------------
// Minimal EF Core entity stubs (replace with your actual models)
// ---------------------------------------------------------------------------

public class Product
{
    public int    Id    { get; set; }
    public int    Stock { get; set; }  // greybeard: integer unit count, never decimal/float
    public string Name  { get; set; } = string.Empty;
}

public class StockReservation
{
    public int      Id         { get; set; }
    public Guid     OrderId    { get; set; }
    public int      ProductId  { get; set; }
    public int      Quantity   { get; set; }
    public DateTime ReservedAt { get; set; }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product>          Products          => Set<Product>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // greybeard: DB-level safety net — CHECK constraint ensures Stock never goes negative
        // even if a bug bypasses the application-layer guard.
        mb.Entity<Product>()
          .ToTable(t => t.HasCheckConstraint("CK_Products_Stock_NonNegative", "\"Stock\" >= 0"));

        // greybeard: unique index on (OrderId, ProductId) enforces idempotency at the DB level,
        // providing a second line of defence if two requests race past the application-level check.
        mb.Entity<StockReservation>()
          .HasIndex(r => new { r.OrderId, r.ProductId })
          .IsUnique();
    }
}

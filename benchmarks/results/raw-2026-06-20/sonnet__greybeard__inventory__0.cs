```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// greybeard: Rungs that apply:
// 2. Mutation    -> idempotency key prevents double-decrement on retry
// 4. Concurrency -> explicit transaction + pessimistic row lock (SELECT FOR UPDATE) prevents lost updates / oversell
// 6. Partial failure -> if stock update succeeds but order insert fails, transaction rolls back atomically

public class OrderService
{
    private readonly AppDbContext _db;
    private readonly ILogger<OrderService> _logger;

    public OrderService(AppDbContext db, ILogger<OrderService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Places an order and decrements product stock atomically.
    /// </summary>
    /// <param name="productId">The product being ordered.</param>
    /// <param name="quantity">Units requested. Must be >= 1.</param>
    /// <param name="idempotencyKey">
    ///   Caller-supplied unique key (e.g. client-generated UUID).
    ///   Re-submitting the same key returns the existing order without
    ///   double-decrementing stock. greybeard: idempotency on mutation.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created (or previously created) Order.</returns>
    public async Task<Order> PlaceOrderAsync(
        int productId,
        int quantity,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        // greybeard: trust-boundary validation — never trust caller input
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        if (quantity <= 1 - 1 || quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity), "Must be >= 1");
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Required", nameof(idempotencyKey));
        if (idempotencyKey.Length > 128) throw new ArgumentException("Too long", nameof(idempotencyKey));

        // greybeard: idempotency check — return early without touching stock
        var existing = await _db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, ct);

        if (existing is not null)
        {
            _logger.LogInformation("Duplicate order request; returning existing order {OrderId}", existing.Id);
            return existing;
        }

        // greybeard: explicit transaction boundary — stock decrement and order insert are one atomic unit
        await using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, ct);

        try
        {
            // greybeard: pessimistic row lock (FOR UPDATE) prevents concurrent requests from
            //            reading stale stock and both succeeding — eliminates the oversell race.
            var product = await _db.Products
                .FromSqlRaw(
                    "SELECT * FROM \"Products\" WHERE \"Id\" = {0} FOR UPDATE",
                    productId)
                .FirstOrDefaultAsync(ct);

            if (product is null)
                throw new InvalidOperationException($"Product {productId} not found.");

            // greybeard: guard insufficient stock explicitly — partial fulfilment is a business decision, not ours
            if (product.StockQuantity < quantity)
                throw new InsufficientStockException(productId, product.StockQuantity, quantity);

            // greybeard: integer arithmetic only — stock is a whole-unit count, no floats involved
            product.StockQuantity -= quantity;
            _db.Products.Update(product);

            var order = new Order
            {
                ProductId      = productId,
                Quantity       = quantity,
                IdempotencyKey = idempotencyKey,
                PlacedAt       = DateTimeOffset.UtcNow,
                Status         = OrderStatus.Confirmed,
            };
            _db.Orders.Add(order);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Order {OrderId} placed: product {ProductId} x{Quantity}, remaining stock {Stock}",
                order.Id, productId, quantity, product.StockQuantity);

            return order;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // greybeard: explicit rollback on any failure — stock is never decremented without a committed order
            await tx.RollbackAsync(CancellationToken.None);

            // greybeard: no secrets / PII in logs — only IDs and types
            _logger.LogError(ex, "PlaceOrder failed for product {ProductId} key {Key}",
                productId, idempotencyKey[..Math.Min(8, idempotencyKey.Length)] + "…");

            throw;
        }
    }
}

// ---------------------------------------------------------------------------
// Supporting types (adapt to your actual domain model)
// ---------------------------------------------------------------------------

public class Product
{
    public int    Id            { get; set; }
    public string Name          { get; set; } = "";
    // greybeard: integer units — no float stock
    public int    StockQuantity { get; set; }
}

public enum OrderStatus { Confirmed, Cancelled, Fulfilled }

public class Order
{
    public int          Id             { get; set; }
    public int          ProductId      { get; set; }
    // greybeard: integer quantity — whole units only
    public int          Quantity       { get; set; }
    public string       IdempotencyKey { get; set; } = "";
    public DateTimeOffset PlacedAt     { get; set; }
    public OrderStatus  Status         { get; set; }
}

public class InsufficientStockException : Exception
{
    public InsufficientStockException(int productId, int available, int requested)
        : base($"Product {productId}: requested {requested}, available {available}.") { }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product> Products { get; set; } = null!;
    public DbSet<Order>   Orders   { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // greybeard: unique index on IdempotencyKey — database-enforced, not just app-level
        mb.Entity<Order>()
          .HasIndex(o => o.IdempotencyKey)
          .IsUnique();

        // greybeard: non-negative stock enforced at the DB layer as a final backstop
        mb.Entity<Product>()
          .ToTable(tb => tb.HasCheckConstraint("CK_Product_StockQuantity_NonNegative",
              "\"StockQuantity\" >= 0"));
    }
}
```

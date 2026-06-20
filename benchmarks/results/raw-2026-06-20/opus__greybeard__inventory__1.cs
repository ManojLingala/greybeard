using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory;

// greybeard ladder applied to "decrement stock on order placed":
//   1. Money?            -> N/A. We move integer *units* of stock, not currency. (no float anywhere)
//   2. Mutation?         -> YES. This is a state mutation triggered by an order. Idempotent on OrderId
//                           so a retried request / duplicate webhook does not double-decrement.
//   3. External call?    -> N/A. Pure DB work.
//   4. Concurrency?      -> YES. Two orders racing for the last unit must not oversell. Optimistic
//                           concurrency via [Timestamp]/RowVersion + bounded retry, all inside one tx.
//   5. Reads a list?     -> Bounded: we read only the exact products in this order, in one batched query (no N+1).
//   6. Can it fail half-way? -> YES. A multi-line order is all-or-nothing. One transaction; any
//                           insufficient-stock line aborts the whole decrement. No partial commit.
//   7. Minimum correct code, made observable.

public sealed class InsufficientStockException : Exception
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

public sealed record OrderLine(int ProductId, int Quantity);

public sealed class StockService
{
    private readonly InventoryDbContext _db;
    private readonly ILogger<StockService> _logger;

    // greybeard: small bounded retry budget for the optimistic-concurrency race. Never unbounded.
    private const int MaxConcurrencyRetries = 3;

    public StockService(InventoryDbContext db, ILogger<StockService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Decrements product stock for the given order. Atomic across all lines, idempotent per orderId,
    /// and safe against concurrent orders competing for the same stock.
    /// </summary>
    public async Task DecrementStockForOrderAsync(
        Guid orderId,
        IReadOnlyCollection<OrderLine> lines,
        CancellationToken ct = default)
    {
        // greybeard: trust-boundary validation. Reject malformed input before touching any row.
        if (orderId == Guid.Empty)
            throw new ArgumentException("orderId is required.", nameof(orderId));
        if (lines is null || lines.Count == 0)
            throw new ArgumentException("Order must contain at least one line.", nameof(lines));
        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
                throw new ArgumentException(
                    $"Quantity for product {line.ProductId} must be positive (got {line.Quantity}).",
                    nameof(lines));
        }

        // greybeard: coalesce duplicate product lines so we lock/decrement each row exactly once.
        // Integer units only — no float math anywhere in stock accounting.
        var requested = lines
            .GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(l => (long)l.Quantity));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await TryDecrementOnceAsync(orderId, requested, ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                // greybeard: lost-update guard fired — another order changed a row first.
                // Reset tracked state and re-read fresh stock, then retry within budget.
                _logger.LogWarning(
                    "Stock decrement for order {OrderId} hit a concurrency conflict (attempt {Attempt}/{Max}); retrying.",
                    orderId, attempt, MaxConcurrencyRetries);

                foreach (var entry in _db.ChangeTracker.Entries().ToList())
                    entry.State = EntityState.Detached;
            }
        }
    }

    private async Task TryDecrementOnceAsync(
        Guid orderId,
        IReadOnlyDictionary<int, long> requested,
        CancellationToken ct)
    {
        // greybeard: explicit transaction boundary — the whole order's decrement commits or none of it does.
        await using var tx = await _db.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct)
            .ConfigureAwait(false);

        // greybeard: idempotency. If we already recorded this order's reservation, this call is a
        // retry/duplicate — do nothing and return success. Exactly-once effect, at-least-once delivery.
        var alreadyApplied = await _db.StockReservations
            .AsNoTracking()
            .AnyAsync(r => r.OrderId == orderId, ct)
            .ConfigureAwait(false);
        if (alreadyApplied)
        {
            _logger.LogInformation("Order {OrderId} stock already decremented; treating as idempotent no-op.", orderId);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        // greybeard: bounded batched read — only the products in THIS order, one round trip, no N+1.
        var productIds = requested.Keys.ToArray();
        var products = await _db.Products
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct)
            .ConfigureAwait(false);

        // greybeard: fail-closed on unknown product — never silently skip a line.
        foreach (var id in productIds)
        {
            if (!products.ContainsKey(id))
                throw new KeyNotFoundException($"Product {id} does not exist.");
        }

        // greybeard: validate sufficiency for EVERY line before mutating any, so a partial order
        // never half-commits. Stock is an integer; check is exact, no rounding.
        foreach (var (productId, qty) in requested)
        {
            var product = products[productId];
            if (product.StockOnHand < qty)
                throw new InsufficientStockException(productId, checked((int)qty), product.StockOnHand);
        }

        foreach (var (productId, qty) in requested)
            products[productId].StockOnHand -= checked((int)qty);

        // greybeard: the reservation row IS the idempotency record AND the recovery breadcrumb.
        // A unique constraint on OrderId is the last line of defense if two retries race past the read.
        _db.StockReservations.Add(new StockReservation
        {
            OrderId = orderId,
            CreatedAtUtc = DateTime.UtcNow,
        });

        // greybeard: SaveChanges enforces RowVersion optimistic concurrency; a stale row throws
        // DbUpdateConcurrencyException and is retried by the caller. No lost updates, no oversell.
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Decremented stock for order {OrderId} across {LineCount} product(s).",
            orderId, requested.Count);
    }
}

public sealed class Product
{
    public int Id { get; set; }

    // greybeard: integer units. Stock is never a floating-point quantity.
    public int StockOnHand { get; set; }

    // greybeard: optimistic-concurrency token. EF bumps/checks this on every UPDATE -> lost-update safe.
    [Microsoft.EntityFrameworkCore.Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

// greybeard: idempotency + audit record. Unique index on OrderId makes double-application impossible
// even under a perfect race; also serves as the recovery trail for reconciliation.
public sealed class StockReservation
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // greybeard: enforce idempotency at the database, not just in app logic.
        modelBuilder.Entity<StockReservation>()
            .HasIndex(r => r.OrderId)
            .IsUnique();
    }
}

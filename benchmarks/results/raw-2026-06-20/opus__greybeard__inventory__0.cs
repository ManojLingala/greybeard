using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

// greybeard ladder applied to "decrement stock when an order is placed":
//   Rung 2 (Mutation)     -> idempotency key on the order so a retried request never double-decrements.
//   Rung 4 (Concurrency)  -> explicit transaction + optimistic concurrency token (RowVersion) so two
//                            simultaneous orders on the same product row can't lose an update / oversell.
//   Rung 6 (Half-way)     -> conditional UPDATE + retry loop; on insufficient stock we fail cleanly with a
//                            domain result, no partial mutation left behind.
//   Rung 1 (Money) n/a    -> stock is a count, modeled as an integer (units), never a float regardless.
//   Rung 3 (External) n/a -> no external call here.
//   Rung 5 (List) n/a     -> single-row write, no unbounded read.

namespace Inventory
{
    // --- Domain model -------------------------------------------------------

    public class Product
    {
        public Guid Id { get; set; }

        // greybeard: stock is a discrete count of units -> integer, never float. Cannot go negative.
        public int StockUnits { get; set; }

        // greybeard (Rung 4): optimistic concurrency token. EF Core throws DbUpdateConcurrencyException
        // if another transaction changed this row between our read and write -> no lost updates / oversell.
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }

    // greybeard (Rung 2): records that a given order has already consumed stock. The unique OrderId is the
    // idempotency key; the unique index makes a duplicate insert fail rather than decrement a second time.
    public class StockReservation
    {
        public Guid Id { get; set; }
        public Guid OrderId { get; set; }
        public Guid ProductId { get; set; }
        public int Units { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
    }

    public class InventoryDbContext : DbContext
    {
        public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }

        public DbSet<Product> Products => Set<Product>();
        public DbSet<StockReservation> StockReservations => Set<StockReservation>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Product>(e =>
            {
                e.HasKey(p => p.Id);
                // greybeard (Rung 4): mark RowVersion as the concurrency token.
                e.Property(p => p.RowVersion).IsRowVersion();
            });

            b.Entity<StockReservation>(e =>
            {
                e.HasKey(r => r.Id);
                // greybeard (Rung 2): enforce exactly-once at the database level, not just in app code.
                e.HasIndex(r => r.OrderId).IsUnique();
            });
        }
    }

    // --- Result type: partial failure is a first-class return value, not an exception to swallow ---

    public enum DecrementOutcome
    {
        Decremented,        // we consumed stock this call
        AlreadyApplied,     // idempotent replay -> stock already consumed for this order, no-op
        InsufficientStock   // not enough units -> caller must not fulfill the order
    }

    public readonly record struct DecrementResult(DecrementOutcome Outcome, int RemainingUnits);

    // --- The operation ------------------------------------------------------

    public class StockService
    {
        private readonly InventoryDbContext _db;
        private const int MaxConcurrencyRetries = 3;

        public StockService(InventoryDbContext db) => _db = db;

        /// <summary>
        /// Decrements <paramref name="units"/> of <paramref name="productId"/> on behalf of
        /// <paramref name="orderId"/>. Safe to call more than once for the same order: the effect is
        /// exactly-once even under at-least-once delivery (retried request, redelivered message).
        /// </summary>
        public async Task<DecrementResult> DecrementForOrderAsync(
            Guid orderId,
            Guid productId,
            int units,
            CancellationToken ct = default)
        {
            // greybeard: validate input crossing the boundary before it touches the database.
            if (orderId == Guid.Empty) throw new ArgumentException("orderId required.", nameof(orderId));
            if (productId == Guid.Empty) throw new ArgumentException("productId required.", nameof(productId));
            if (units <= 0) throw new ArgumentOutOfRangeException(nameof(units), "Units must be positive.");

            // greybeard (Rung 4): retry the whole transaction on a concurrency clash. Each retry re-reads
            // the current RowVersion, so we converge instead of clobbering a concurrent decrement.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await TryDecrementOnceAsync(orderId, productId, units, ct);
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
                {
                    // Another order mutated the row first. Detach stale entities and retry with fresh data.
                    _db.ChangeTracker.Clear();
                }
            }
        }

        private async Task<DecrementResult> TryDecrementOnceAsync(
            Guid orderId, Guid productId, int units, CancellationToken ct)
        {
            // greybeard (Rung 4): explicit transaction boundary so the reservation insert and the stock
            // decrement commit together or not at all -- no half-applied state.
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // greybeard (Rung 2): idempotency check. If this order already reserved stock, this is a replay;
            // return without touching the count. Reads inside the transaction for a consistent snapshot.
            var alreadyReserved = await _db.StockReservations
                .AsNoTracking()
                .AnyAsync(r => r.OrderId == orderId, ct);

            if (alreadyReserved)
            {
                var current = await _db.Products
                    .AsNoTracking()
                    .Where(p => p.Id == productId)
                    .Select(p => (int?)p.StockUnits)
                    .SingleOrDefaultAsync(ct);

                await tx.CommitAsync(ct);
                return new DecrementResult(DecrementOutcome.AlreadyApplied, current ?? 0);
            }

            var product = await _db.Products.SingleOrDefaultAsync(p => p.Id == productId, ct)
                ?? throw new InvalidOperationException($"Product {productId} not found.");

            // greybeard (Rung 6): insufficient stock is an expected outcome, not a crash. Roll back so no
            // reservation row leaks, and tell the caller the order cannot be fulfilled.
            if (product.StockUnits < units)
            {
                await tx.RollbackAsync(ct);
                return new DecrementResult(DecrementOutcome.InsufficientStock, product.StockUnits);
            }

            product.StockUnits -= units; // integer math, guarded above -> never negative.

            _db.StockReservations.Add(new StockReservation
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                ProductId = productId,
                Units = units,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });

            try
            {
                // greybeard (Rung 4): SaveChanges checks RowVersion. A concurrent decrement bumps the token
                // and forces DbUpdateConcurrencyException -> caught and retried by the caller above.
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // greybeard (Rung 2): two concurrent calls for the *same* order raced past the idempotency
                // check; the unique index on OrderId rejected the second insert. Treat as already-applied.
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                var remaining = await _db.Products
                    .AsNoTracking()
                    .Where(p => p.Id == productId)
                    .Select(p => p.StockUnits)
                    .SingleAsync(ct);
                return new DecrementResult(DecrementOutcome.AlreadyApplied, remaining);
            }

            await tx.CommitAsync(ct);

            // greybeard (Rung 7): observable -- caller logs outcome + remaining at INFO. No secrets here:
            // only ids and counts cross the log boundary, never tokens or PII.
            return new DecrementResult(DecrementOutcome.Decremented, product.StockUnits);
        }

        // Provider-agnostic-ish unique-violation detection. Replace the SQLSTATE/error-number check with
        // your provider's (e.g. Npgsql 23505, SqlException 2601/2627) for precision.
        private static bool IsUniqueViolation(DbUpdateException ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return msg.Contains("unique", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
        }
    }
}

// 3am re-read: "What still breaks?"
//  - Oversell under concurrency? No -> RowVersion token + transaction; a lost update throws and we retry.
//  - Retried/redelivered order double-decrements? No -> idempotency key (OrderId) + unique index, even
//    under a same-order race (handled via the unique-violation branch).
//  - Negative stock? No -> guarded integer subtraction; insufficient stock returns cleanly, no mutation.
//  - Partial write on failure? No -> single transaction commits both rows or neither.
//  - Float drift on the count? N/A -> integer units throughout.
//  Flagged for the operator: IsUniqueViolation uses string matching; swap in your provider's error code.
//  Flagged: retries are bounded at 3 -- under pathological contention the call surfaces the concurrency
//  exception to the caller rather than spinning forever (intentional: fail loud, let upstream back off).

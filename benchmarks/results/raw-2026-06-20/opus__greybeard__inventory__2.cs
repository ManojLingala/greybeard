using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// greybeard ladder for "decrement stock when an order is placed":
//   1. Money?      -> no money moves here (stock counts are integers already; pricing lives elsewhere). Skip.
//   2. Mutation?   -> YES. Placing an order is a state mutation that will be retried (network blips, at-least-once
//                     queues, double-clicks). We take an idempotency key and make the effect exactly-once.
//   3. External?   -> no outbound call in this method. Skip.
//   4. Concurrency?-> YES. Two orders can hit the same product row at once -> lost update / overselling.
//                     We guard with a conditional UPDATE (atomic compare-and-decrement) so the database, not the
//                     app, enforces "never go below zero". Optimistic concurrency token catches concurrent writes.
//   5. Reads list? -> we read N order lines (bounded by the order itself). We batch-load to avoid N+1.
//   6. Half-way?   -> YES. If line 3 of 5 oversells, the whole order must fail and nothing is decremented.
//                     One transaction = all-or-nothing. No compensating saga needed because it is one local DB.
//   7. Minimum correct code, observable (structured logs, no secrets, no PII).

namespace Inventory
{
    public sealed class OrderLine
    {
        public Guid ProductId { get; init; }
        public int Quantity { get; init; }
    }

    public sealed class Product
    {
        public Guid Id { get; set; }
        public int StockOnHand { get; set; }

        // greybeard (rung 4): optimistic-concurrency token. EF maps this as [Timestamp]/rowversion so two
        // concurrent decrements on the same row cannot silently clobber each other -> DbUpdateConcurrencyException.
        public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Records that we have already processed a given idempotency key, so a retried order placement is a no-op.
    /// greybeard (rung 2): exactly-once effect, at-least-once delivery. Unique index on IdempotencyKey.
    /// </summary>
    public sealed class ProcessedOrder
    {
        public long Id { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public Guid OrderId { get; set; }
        public DateTimeOffset ProcessedAtUtc { get; set; }
    }

    public sealed class InventoryDbContext : DbContext
    {
        public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }

        public DbSet<Product> Products => Set<Product>();
        public DbSet<ProcessedOrder> ProcessedOrders => Set<ProcessedOrder>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Product>(e =>
            {
                e.HasKey(p => p.Id);
                // greybeard (rung 4): rowversion concurrency token.
                e.Property(p => p.RowVersion).IsRowVersion();
                // greybeard (rung 4): DB-level invariant so overselling is impossible even outside this method.
                e.ToTable(t => t.HasCheckConstraint("ck_products_stock_nonneg", "[StockOnHand] >= 0"));
            });

            b.Entity<ProcessedOrder>(e =>
            {
                e.HasKey(p => p.Id);
                // greybeard (rung 2): unique idempotency key -> the dedupe is enforced by the database,
                // not by a check-then-act race in application code.
                e.HasIndex(p => p.IdempotencyKey).IsUnique();
                e.Property(p => p.IdempotencyKey).HasMaxLength(200).IsRequired();
            });
        }
    }

    public enum DecrementOutcome
    {
        /// <summary>Stock decremented and order recorded for the first time.</summary>
        Applied,
        /// <summary>This idempotency key was already processed; this call was a safe no-op.</summary>
        AlreadyProcessed,
    }

    public sealed class InsufficientStockException : Exception
    {
        public Guid ProductId { get; }
        public int Requested { get; }
        public int Available { get; }

        public InsufficientStockException(Guid productId, int requested, int available)
            : base($"Insufficient stock for product {productId}: requested {requested}, available {available}.")
        {
            ProductId = productId;
            Requested = requested;
            Available = available;
        }
    }

    public sealed class InventoryService
    {
        private readonly InventoryDbContext _db;
        private readonly ILogger<InventoryService> _log;

        public InventoryService(InventoryDbContext db, ILogger<InventoryService> log)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>
        /// Decrements stock for every line of an order, atomically. Either every line is decremented or none is.
        /// Safe to call more than once with the same <paramref name="idempotencyKey"/>.
        /// </summary>
        /// <param name="orderId">The order being placed (for traceability).</param>
        /// <param name="lines">The order lines. Each (ProductId, Quantity) is validated and aggregated.</param>
        /// <param name="idempotencyKey">Stable per-order key so retried placements do not double-decrement.</param>
        public async Task<DecrementOutcome> DecrementStockForOrderAsync(
            Guid orderId,
            IReadOnlyCollection<OrderLine> lines,
            string idempotencyKey,
            CancellationToken ct = default)
        {
            // greybeard (trust boundary): validate everything crossing in. Do not trust the caller's happy path.
            if (orderId == Guid.Empty)
                throw new ArgumentException("orderId is required.", nameof(orderId));
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                throw new ArgumentException("idempotencyKey is required.", nameof(idempotencyKey));
            if (lines is null || lines.Count == 0)
                throw new ArgumentException("Order must contain at least one line.", nameof(lines));

            // greybeard (rung 4/6): collapse duplicate product lines and reject non-positive quantities up front,
            // so the same product is decremented exactly once and we never "decrement by zero/negative".
            var demand = new Dictionary<Guid, int>();
            foreach (var line in lines)
            {
                if (line.ProductId == Guid.Empty)
                    throw new ArgumentException("Order line has empty ProductId.", nameof(lines));
                if (line.Quantity <= 0)
                    throw new ArgumentException(
                        $"Order line quantity must be positive (product {line.ProductId}).", nameof(lines));

                demand[line.ProductId] = demand.TryGetValue(line.ProductId, out var q)
                    ? checked(q + line.Quantity) // greybeard: checked -> overflow on a hostile/huge order surfaces, not wraps.
                    : line.Quantity;
            }

            // greybeard (rung 2): short-circuit known-processed keys before opening a transaction. This is a fast path;
            // the authoritative dedupe is the unique-index insert inside the transaction below (handles the race).
            var seen = await _db.ProcessedOrders
                .AsNoTracking()
                .AnyAsync(p => p.IdempotencyKey == idempotencyKey, ct);
            if (seen)
            {
                _log.LogInformation(
                    "Order {OrderId} idempotency key already processed; no-op. (KeyHash {KeyHash})",
                    orderId, HashForLog(idempotencyKey)); // greybeard (no secrets): log a hash, not the raw key.
                return DecrementOutcome.AlreadyProcessed;
            }

            // greybeard (rung 6): one transaction = all-or-nothing. A half-decremented order is never observable.
            await using var tx = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct);

            try
            {
                // greybeard (rung 5/N+1): batch-load all needed products in one query, then process in a stable order.
                var productIds = demand.Keys.ToArray();
                var products = await _db.Products
                    .Where(p => productIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, ct);

                // greybeard (trust boundary): a referenced product that does not exist is a hard error, not a skip.
                foreach (var id in productIds)
                {
                    if (!products.ContainsKey(id))
                        throw new InvalidOperationException($"Product {id} not found.");
                }

                // greybeard (rung 4): process in deterministic ProductId order to avoid deadlocks under concurrency
                // (all callers acquire row locks in the same sequence).
                foreach (var id in productIds.OrderBy(x => x))
                {
                    var qty = demand[id];
                    var product = products[id];

                    // greybeard (rung 4): atomic compare-and-decrement at the database. We do NOT read-modify-write
                    // in memory (that is the classic lost-update / oversell). The WHERE clause makes the DB reject
                    // the update if stock would go negative; affected-rows == 0 means lose-the-race or not-enough.
                    var affected = await _db.Products
                        .Where(p => p.Id == id && p.StockOnHand >= qty)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(p => p.StockOnHand, p => p.StockOnHand - qty),
                            ct);

                    if (affected == 0)
                    {
                        // Re-read the live value purely for an honest error message; the transaction will roll back.
                        var available = await _db.Products
                            .Where(p => p.Id == id)
                            .Select(p => p.StockOnHand)
                            .SingleAsync(ct);

                        _log.LogWarning(
                            "Order {OrderId} rejected: insufficient stock for product {ProductId} " +
                            "(requested {Requested}, available {Available}).",
                            orderId, id, qty, available);

                        // greybeard (rung 6): throwing rolls back every prior decrement in this order. No partial commit.
                        throw new InsufficientStockException(id, qty, available);
                    }
                }

                // greybeard (rung 2): record the idempotency key INSIDE the transaction. The unique index turns a
                // concurrent duplicate placement into a DbUpdateException -> we treat it as AlreadyProcessed, so the
                // effect is exactly-once even when two retries race past the fast-path check above.
                _db.ProcessedOrders.Add(new ProcessedOrder
                {
                    IdempotencyKey = idempotencyKey,
                    OrderId = orderId,
                    ProcessedAtUtc = DateTimeOffset.UtcNow,
                });

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _log.LogInformation(
                    "Order {OrderId} decremented stock for {LineCount} product(s).",
                    orderId, productIds.Length);

                return DecrementOutcome.Applied;
            }
            catch (DbUpdateException ex) when (IsUniqueKeyViolation(ex))
            {
                // greybeard (rung 2): lost the idempotency race to a concurrent identical placement.
                // The other transaction already applied the decrement; ours rolls back. Net effect: exactly-once.
                await tx.RollbackAsync(CancellationToken.None);
                _log.LogInformation(
                    "Order {OrderId} idempotency key won by a concurrent request; treating as already processed.",
                    orderId);
                return DecrementOutcome.AlreadyProcessed;
            }
            catch
            {
                // greybeard (data-loss safety): any failure rolls the whole order back. The DB returns to its prior
                // state and the caller may safely retry with the same idempotency key.
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        // greybeard (no secrets in logs): never log the raw idempotency key; emit a short, non-reversible fingerprint.
        private static string HashForLog(string value)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes, 0, 4); // first 4 bytes is enough to correlate, not enough to reconstruct.
        }

        // greybeard: provider-agnostic-ish detection of unique-constraint violation. In real code, narrow this to your
        // provider's error number (e.g. SqlException 2601/2627, Postgres 23505) rather than string-sniffing.
        private static bool IsUniqueKeyViolation(DbUpdateException ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return msg.Contains("unique", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
        }
    }
}

// greybeard re-read at 3am — "what still breaks?":
//  - Oversell under load: prevented. The atomic `WHERE StockOnHand >= qty` + check constraint makes negative stock
//    unrepresentable; the app cannot win a TOCTOU race it never performs.
//  - Double-charge / double-decrement on retry: prevented. Idempotency key is inserted in the same transaction,
//    guarded by a unique index; concurrent duplicates resolve to AlreadyProcessed.
//  - Partial order: prevented. Single transaction; any failing line rolls back all prior lines.
//  - Deadlocks: mitigated by touching rows in sorted ProductId order.
//  - Secrets/PII in logs: idempotency key is hashed; no amounts or customer data logged.
//  - Cancellation: ct threaded through; rollback on the failure path uses CancellationToken.None so cleanup is not
//    itself cancelled mid-flight.
//  Open flags for the reviewer: IsUniqueKeyViolation should be tightened to the concrete DB provider's error code.

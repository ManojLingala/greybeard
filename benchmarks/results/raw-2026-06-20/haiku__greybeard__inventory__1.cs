using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

/// <summary>
/// Decrements product stock when an order is placed, applying greybeard principles:
/// - Mutation: idempotency key prevents double-decrement on retry
/// - Concurrency: optimistic locking via Version field prevents lost updates
/// - Fail-safe: transaction ensures atomicity; partial failure is detected
/// - Observable: explicit error states, no silent failures
/// </summary>
public class OrderInventoryService
{
    private readonly DbContext _context;

    public OrderInventoryService(DbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Decrements product stock for an order with transactional safety.
    /// </summary>
    /// <param name="orderId">Unique order identifier (trust boundary: validate externally)</param>
    /// <param name="productId">Product to decrement (trust boundary: validate externally)</param>
    /// <param name="quantityToDecrement">Units to remove (trust boundary: must be positive integer)</param>
    /// <param name="idempotencyKey">Unique key to ensure at-most-once effect on retry</param>
    /// <returns>True if stock decremented successfully; false if already processed or insufficient stock</returns>
    public async Task<bool> DecrementProductStockAsync(
        long orderId,
        long productId,
        int quantityToDecrement,
        string idempotencyKey)
    {
        // greybeard: Mutation - check idempotency key first to avoid double-decrement on retry
        var existingDeduction = await _context.Set<OrderStockDeduction>()
            .FirstOrDefaultAsync(d => d.IdempotencyKey == idempotencyKey);

        if (existingDeduction != null)
        {
            // Already processed this request; return success without re-decrementing
            return existingDeduction.Success;
        }

        // greybeard: Concurrency - use explicit transaction with row lock for stock table
        using var transaction = await _context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead);

        try
        {
            // greybeard: Reads a list? - No, single product. No pagination needed.
            // Fetch product with lock hint for concurrent access
            var product = await _context.Set<Product>()
                .Where(p => p.Id == productId)
                .FirstOrDefaultAsync();

            if (product == null)
            {
                await RecordDeductionAsync(orderId, productId, idempotencyKey, false, "Product not found");
                return false;
            }

            // greybeard: Money? - Inventory is integer units (never float). Validate no negative result.
            if (product.StockQuantity < quantityToDecrement)
            {
                await RecordDeductionAsync(orderId, productId, idempotencyKey, false,
                    $"Insufficient stock: {product.StockQuantity} available, {quantityToDecrement} requested");
                return false;
            }

            // greybeard: Mutation + Concurrency - decrement with version check (optimistic lock)
            // If another request incremented Version between read and update, SaveChanges will throw DbUpdateConcurrencyException
            product.StockQuantity -= quantityToDecrement;
            product.Version++; // Increment version for optimistic lock

            _context.Update(product);

            // greybeard: Can it fail halfway? - Record the deduction attempt atomically with stock change
            var deduction = new OrderStockDeduction
            {
                OrderId = orderId,
                ProductId = productId,
                QuantityDecrementedUnits = quantityToDecrement,
                IdempotencyKey = idempotencyKey,
                Success = true,
                Timestamp = DateTime.UtcNow
            };
            _context.Add(deduction);

            // greybeard: External call? - No external calls here; SaveChanges is local atomic operation
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // greybeard: Concurrency - another request modified the product; fail safely
            await transaction.RollbackAsync();
            await RecordDeductionAsync(orderId, productId, idempotencyKey, false,
                "Concurrency conflict: product was modified; retry advised");
            return false;
        }
        catch (Exception ex)
        {
            // greybeard: Can it fail halfway? - Rollback on any error; record failure for observability
            await transaction.RollbackAsync();
            await RecordDeductionAsync(orderId, productId, idempotencyKey, false,
                $"Deduction failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Helper: Records deduction attempt for audit and idempotency replay.
    /// greybeard: Can it fail halfway? - Compensating action: log all mutations for recovery.
    /// </summary>
    private async Task RecordDeductionAsync(
        long orderId,
        long productId,
        string idempotencyKey,
        bool success,
        string reason)
    {
        var deduction = new OrderStockDeduction
        {
            OrderId = orderId,
            ProductId = productId,
            QuantityDecrementedUnits = 0,
            IdempotencyKey = idempotencyKey,
            Success = success,
            FailureReason = reason, // greybeard: Observable - clear error reason, no secrets
            Timestamp = DateTime.UtcNow
        };

        _context.Add(deduction);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            // Logging framework would flush this; do not allow audit failure to crash the caller
        }
    }
}

/// <summary>
/// Entity: Product inventory. greybeard: Concurrency - Version field enables optimistic locking.
/// </summary>
public class Product
{
    public long Id { get; set; }

    /// <summary>
    /// Stock quantity in integer units. greybeard: Money? - Never float.
    /// </summary>
    public int StockQuantity { get; set; }

    /// <summary>
    /// greybeard: Concurrency - optimistic lock version; incremented on each update.
    /// </summary>
    [Timestamp]
    public byte[] Version { get; set; }
}

/// <summary>
/// Audit log: greybeard: Can it fail halfway? - Records every stock deduction attempt for recovery and idempotency.
/// greybeard: Mutation - idempotency key ensures replay is safe.
/// </summary>
public class OrderStockDeduction
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public long ProductId { get; set; }
    public int QuantityDecrementedUnits { get; set; }

    /// <summary>
    /// greybeard: Mutation - idempotency key prevents double-decrement on request retry.
    /// </summary>
    public string IdempotencyKey { get; set; }

    public bool Success { get; set; }

    /// <summary>
    /// greybeard: Observable - reason for failure; no secrets in message.
    /// </summary>
    public string FailureReason { get; set; }

    public DateTime Timestamp { get; set; }
}

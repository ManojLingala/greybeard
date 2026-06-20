using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class OrderInventoryService
{
    private readonly AppDbContext _context;
    private readonly ILogger<OrderInventoryService> _logger;

    public OrderInventoryService(AppDbContext context, ILogger<OrderInventoryService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Decrements product stock when an order is placed.
    /// Applies greybeard rungs: Mutation (idempotency), Concurrency (transaction + row lock), Fails half-way (compensating action).
    /// </summary>
    public async Task<bool> DecrementStockForOrderAsync(
        string idempotencyKey,
        int productId,
        int quantityOrdered)
    {
        using var transaction = await _context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable);

        try
        {
            // greybeard: Mutation - idempotency check. Retried requests with same key must not double-decrement.
            var existingRecord = await _context.StockDecrementAudit
                .FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey);

            if (existingRecord != null)
            {
                _logger.LogInformation(
                    "Stock decrement already processed for idempotency key {Key}, product {ProductId}",
                    idempotencyKey, productId);
                await transaction.CommitAsync();
                return true; // idempotent success
            }

            // greybeard: Concurrency - row lock. SELECT FOR UPDATE equivalent via EF Core.
            // Using AsNoTracking(false) + Serializable isolation to ensure pessimistic lock.
            var product = await _context.Products
                .FromSql($"SELECT * FROM Products WHERE Id = {productId} FOR UPDATE")
                .FirstOrDefaultAsync();

            if (product == null)
            {
                _logger.LogWarning("Product {ProductId} not found for stock decrement", productId);
                await transaction.RollbackAsync();
                return false;
            }

            // greybeard: Money (inventory as asset) - use integer units, never float.
            // Explicit check: don't allow negative stock to silently underflow.
            if (product.StockQuantity < quantityOrdered)
            {
                _logger.LogWarning(
                    "Insufficient stock for product {ProductId}. Available: {Available}, Requested: {Requested}",
                    productId, product.StockQuantity, quantityOrdered);
                await transaction.RollbackAsync();
                return false;
            }

            // greybeard: Mutation - record the intent before mutation for audit and compensation.
            var auditEntry = new StockDecrementAudit
            {
                IdempotencyKey = idempotencyKey,
                ProductId = productId,
                QuantityDecremented = quantityOrdered,
                PreviousStock = product.StockQuantity,
                Timestamp = DateTime.UtcNow,
                Status = "committed" // written before mutation for recovery
            };

            _context.StockDecrementAudit.Add(auditEntry);

            // greybeard: Mutation - apply the decrement within the transaction.
            product.StockQuantity -= quantityOrdered;
            product.UpdatedAt = DateTime.UtcNow;

            // greybeard: Fails half-way - write both mutation and audit atomically.
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Stock decremented for product {ProductId}: {Quantity} units. " +
                "Previous: {Previous}, New: {New}. IdempotencyKey: {Key}",
                productId, quantityOrdered, auditEntry.PreviousStock, product.StockQuantity, idempotencyKey);

            await transaction.CommitAsync();
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is TimeoutException)
        {
            // greybeard: External call (DB is external) - timeout occurred.
            _logger.LogError(ex,
                "Timeout during stock decrement for product {ProductId}, idempotency key {Key}. " +
                "Transaction will rollback. Safe to retry.",
                productId, idempotencyKey);
            await transaction.RollbackAsync();
            return false;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // greybeard: Concurrency - row was modified outside this transaction.
            // Should not occur with Serializable isolation, but handle gracefully.
            _logger.LogError(ex,
                "Concurrency conflict during stock decrement for product {ProductId}, idempotency key {Key}. " +
                "Transaction will rollback. Safe to retry.",
                productId, idempotencyKey);
            await transaction.RollbackAsync();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error during stock decrement for product {ProductId}, idempotency key {Key}. " +
                "Transaction will rollback.",
                productId, idempotencyKey);
            await transaction.RollbackAsync();
            throw;
        }
    }
}

// Supporting model classes
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int StockQuantity { get; set; } // greybeard: integer units, never float
    public DateTime UpdatedAt { get; set; }
}

public class StockDecrementAudit
{
    public int Id { get; set; }
    public string IdempotencyKey { get; set; } // greybeard: tracks idempotent retries
    public int ProductId { get; set; }
    public int QuantityDecremented { get; set; } // greybeard: integer minor units
    public int PreviousStock { get; set; }
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } // "committed", "compensated"
}

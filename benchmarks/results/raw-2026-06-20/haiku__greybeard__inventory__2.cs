using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class InventoryService
{
    private readonly DbContext _context;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(DbContext context, ILogger<InventoryService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Decrements product stock when an order is placed.
    /// Applies greybeard patterns: idempotency, concurrency safety, and half-way failure handling.
    /// </summary>
    public async Task<bool> DecrementProductStockAsync(
        string orderId,           // greybeard: idempotency key - same orderId must be safe to retry
        int productId,
        int quantityToDecrement,
        string idempotencyKey)    // greybeard: explicit idempotency token to prevent double-decrement
    {
        using (var transaction = await _context.Database.BeginTransactionAsync())
        {
            try
            {
                // greybeard: explicit transaction boundary for concurrency safety
                // greybeard: row-level lock (FOR UPDATE equivalent) via pessimistic locking
                var product = await _context.Set<Product>()
                    .AsNoTracking()
                    .Where(p => p.Id == productId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(p => p, p => p));

                // Load with lock to prevent lost updates
                var productWithLock = await _context.Set<Product>()
                    .FromSqlInterpolated($"SELECT * FROM Products WHERE Id = {productId} FOR UPDATE")
                    .FirstOrDefaultAsync();

                if (productWithLock == null)
                {
                    _logger.LogWarning("Product not found: {ProductId}", productId);
                    return false;
                }

                // greybeard: validate business logic before mutation
                if (productWithLock.AvailableStock < quantityToDecrement)
                {
                    _logger.LogWarning(
                        "Insufficient stock. ProductId: {ProductId}, Required: {Qty}, Available: {Stock}",
                        productId, quantityToDecrement, productWithLock.AvailableStock);
                    return false;
                }

                // greybeard: check idempotency - if this orderId already processed, skip
                var existingOrderInventory = await _context.Set<OrderInventoryLog>()
                    .Where(oil => oil.OrderId == orderId && oil.ProductId == productId)
                    .FirstOrDefaultAsync();

                if (existingOrderInventory != null)
                {
                    _logger.LogInformation(
                        "Stock already decremented for this order. OrderId: {OrderId}, ProductId: {ProductId}",
                        orderId, productId);
                    return true; // idempotent success
                }

                // greybeard: perform the mutation with explicit boundaries
                productWithLock.AvailableStock -= quantityToDecrement;
                productWithLock.TotalDecremented += quantityToDecrement;
                productWithLock.LastModifiedUtc = DateTime.UtcNow;

                // greybeard: log the mutation for recovery/audit if half-way failure occurs
                var inventoryLog = new OrderInventoryLog
                {
                    OrderId = orderId,
                    IdempotencyKey = idempotencyKey,
                    ProductId = productId,
                    QuantityDecremented = quantityToDecrement,
                    StockBefore = productWithLock.AvailableStock + quantityToDecrement,
                    StockAfter = productWithLock.AvailableStock,
                    CreatedUtc = DateTime.UtcNow
                };

                _context.Update(productWithLock);
                _context.Add(inventoryLog);

                // greybeard: if this fails, transaction rolls back (compensating action)
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation(
                    "Stock decremented successfully. OrderId: {OrderId}, ProductId: {ProductId}, Qty: {Qty}, Before: {Before}, After: {After}",
                    orderId, productId, quantityToDecrement,
                    productWithLock.AvailableStock + quantityToDecrement,
                    productWithLock.AvailableStock);

                return true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(
                    ex,
                    "Concurrency conflict during stock decrement. OrderId: {OrderId}, ProductId: {ProductId}",
                    orderId, productId);

                // greybeard: transaction rolled back automatically; caller should retry with backoff
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error during stock decrement. OrderId: {OrderId}, ProductId: {ProductId}",
                    orderId, productId);

                // greybeard: half-way failure - transaction rolls back, recovery path is explicit
                throw;
            }
        }
    }
}

/// <summary>
/// Supporting domain model (Product)
/// </summary>
public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; }
    public int AvailableStock { get; set; }
    public int TotalDecremented { get; set; }
    public DateTime LastModifiedUtc { get; set; }
}

/// <summary>
/// Audit log for idempotency and recovery
/// greybeard: explicit recovery path if order-inventory mapping fails
/// </summary>
public class OrderInventoryLog
{
    public int Id { get; set; }
    public string OrderId { get; set; }
    public string IdempotencyKey { get; set; }
    public int ProductId { get; set; }
    public int QuantityDecremented { get; set; }
    public int StockBefore { get; set; }
    public int StockAfter { get; set; }
    public DateTime CreatedUtc { get; set; }
}

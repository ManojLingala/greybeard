using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public class InventoryService
{
    private readonly AppDbContext _db;

    public InventoryService(AppDbContext db) => _db = db;

    // Decrements stock for a product when an order is placed.
    // Returns true if the stock was decremented; false if there wasn't enough.
    public async Task<bool> DecrementStockAsync(
        int productId,
        int quantity,
        CancellationToken cancellationToken = default)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        // Do the check-and-decrement in a single UPDATE so concurrent orders
        // can't oversell. The WHERE guards against going negative; the row
        // simply won't match if there isn't enough stock.
        var rowsAffected = await _db.Products
            .Where(p => p.Id == productId && p.Stock >= quantity)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Stock, p => p.Stock - quantity),
                cancellationToken);

        return rowsAffected == 1;
    }
}

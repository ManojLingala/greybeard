using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public class OrderService
{
    private readonly AppDbContext _db;

    public OrderService(AppDbContext db) => _db = db;

    // Decrements stock for a product when an order is placed.
    // Uses an atomic ExecuteUpdate with a guard so we never oversell,
    // and so we don't need to load/track the entity just to subtract.
    public async Task PlaceOrderAsync(int productId, int quantity, CancellationToken ct = default)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");

        var affected = await _db.Products
            .Where(p => p.Id == productId && p.Stock >= quantity)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock - quantity), ct);

        if (affected == 0)
            throw new InvalidOperationException(
                $"Cannot place order: insufficient stock or unknown product {productId}.");
    }
}

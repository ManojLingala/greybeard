using Microsoft.EntityFrameworkCore;

public class OrderService
{
    private readonly AppDbContext _db;

    public OrderService(AppDbContext db)
    {
        _db = db;
    }

    public async Task PlaceOrderAsync(int productId, int quantity)
    {
        var product = await _db.Products.FindAsync(productId)
            ?? throw new InvalidOperationException($"Product {productId} not found.");

        if (product.Stock < quantity)
            throw new InvalidOperationException("Insufficient stock.");

        product.Stock -= quantity;

        _db.Orders.Add(new Order { ProductId = productId, Quantity = quantity });

        await _db.SaveChangesAsync();
    }
}

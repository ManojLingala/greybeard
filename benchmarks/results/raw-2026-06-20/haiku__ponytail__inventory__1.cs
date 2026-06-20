using Microsoft.EntityFrameworkCore;

public class OrderService
{
    private readonly DbContext _context;

    public OrderService(DbContext context)
    {
        _context = context;
    }

    public async Task PlaceOrderAsync(int orderId, int productId, int quantity)
    {
        var product = await _context.Set<Product>()
            .FirstOrDefaultAsync(p => p.Id == productId);

        if (product == null)
            throw new InvalidOperationException("Product not found");

        if (product.Stock < quantity)
            throw new InvalidOperationException("Insufficient stock");

        product.Stock -= quantity;
        await _context.SaveChangesAsync();
    }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Stock { get; set; }
}

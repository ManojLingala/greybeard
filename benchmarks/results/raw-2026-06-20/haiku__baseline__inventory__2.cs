using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;

public class InventoryService
{
    private readonly DbContext _context;

    public InventoryService(DbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Decrements the product stock when an order is placed.
    /// </summary>
    /// <param name="productId">The ID of the product</param>
    /// <param name="quantity">The quantity to decrement</param>
    /// <returns>True if successful, false if insufficient stock</returns>
    public async Task<bool> DecrementProductStockAsync(int productId, int quantity)
    {
        var product = await _context.Set<Product>()
            .FirstOrDefaultAsync(p => p.Id == productId);

        if (product == null)
            return false;

        if (product.Stock < quantity)
            return false;

        product.Stock -= quantity;
        await _context.SaveChangesAsync();

        return true;
    }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Stock { get; set; }
}

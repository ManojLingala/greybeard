using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;

public class OrderService
{
    private readonly DbContext _dbContext;

    public OrderService(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task PlaceOrderAsync(int productId, int quantity)
    {
        var product = await _dbContext.Set<Product>()
            .FirstOrDefaultAsync(p => p.Id == productId);

        if (product == null)
            throw new InvalidOperationException("Product not found");

        if (product.Stock < quantity)
            throw new InvalidOperationException("Insufficient stock");

        product.Stock -= quantity;
        await _dbContext.SaveChangesAsync();
    }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Stock { get; set; }
}

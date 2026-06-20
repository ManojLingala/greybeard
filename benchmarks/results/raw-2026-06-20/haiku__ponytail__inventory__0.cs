using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

public class OrderService
{
    private readonly DbContext _dbContext;

    public OrderService(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task DecrementProductStockAsync(int productId, int quantity)
    {
        var product = await _dbContext.Set<Product>()
            .FirstOrDefaultAsync(p => p.Id == productId);

        if (product != null)
        {
            product.Stock -= quantity;
            await _dbContext.SaveChangesAsync();
        }
    }
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Stock { get; set; }
}

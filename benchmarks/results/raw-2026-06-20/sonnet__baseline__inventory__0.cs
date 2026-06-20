using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace OrderManagement
{
    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Stock { get; set; }
        public decimal Price { get; set; }
    }

    public class Order
    {
        public int Id { get; set; }
        public DateTime OrderDate { get; set; }
        public List<OrderItem> Items { get; set; } = new List<OrderItem>();
    }

    public class OrderItem
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public Order Order { get; set; }
        public int ProductId { get; set; }
        public Product Product { get; set; }
        public int Quantity { get; set; }
    }

    public class AppDbContext : DbContext
    {
        public DbSet<Product> Products { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<OrderItem> OrderItems { get; set; }

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    }

    public class OrderService
    {
        private readonly AppDbContext _context;

        public OrderService(AppDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Places an order and decrements stock for each product in the order.
        /// Throws InvalidOperationException if any product has insufficient stock.
        /// </summary>
        public async Task<Order> PlaceOrderAsync(List<OrderItem> items)
        {
            // Load all relevant products in one query
            var productIds = items.ConvertAll(i => i.ProductId);
            var products = await _context.Products
                .Where(p => productIds.Contains(p.Id))
                .ToListAsync();

            // Validate and decrement stock
            foreach (var item in items)
            {
                var product = products.Find(p => p.Id == item.ProductId);
                if (product == null)
                    throw new InvalidOperationException($"Product with ID {item.ProductId} not found.");

                if (product.Stock < item.Quantity)
                    throw new InvalidOperationException(
                        $"Insufficient stock for product '{product.Name}'. " +
                        $"Available: {product.Stock}, Requested: {item.Quantity}.");

                product.Stock -= item.Quantity;
            }

            var order = new Order
            {
                OrderDate = DateTime.UtcNow,
                Items = items
            };

            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            return order;
        }
    }
}

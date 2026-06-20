using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;
using System.IO;
using System.Threading.Tasks;

[ApiController]
[Route("webhooks/stripe")]
public class StripeWebhookController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly string _webhookSecret;

    public StripeWebhookController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _webhookSecret = config["Stripe:WebhookSecret"]!;
    }

    [HttpPost]
    public async Task<IActionResult> Handle()
    {
        // Read raw body for signature verification
        string json;
        using (var reader = new StreamReader(Request.Body))
            json = await reader.ReadToEndAsync();

        // Verify the Stripe signature to prevent spoofed requests
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _webhookSecret,
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        // Only handle the event type we care about
        if (stripeEvent.Type != Events.PaymentIntentSucceeded)
            return Ok(); // Acknowledge unknown events so Stripe stops retrying them

        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
        if (paymentIntent is null)
            return BadRequest(new { error = "Invalid event data" });

        // Idempotency: skip if we've already processed this event
        // stripeEvent.Id is the stable, unique event ID Stripe guarantees
        bool alreadyProcessed = await _db.ProcessedStripeEvents
            .AnyAsync(e => e.StripeEventId == stripeEvent.Id);

        if (alreadyProcessed)
            return Ok(); // Safe no-op on redelivery

        // Resolve the customer from metadata set at PaymentIntent creation time
        string? customerId = paymentIntent.Metadata.TryGetValue("app_customer_id", out var id)
            ? id : null;

        if (string.IsNullOrEmpty(customerId))
            return BadRequest(new { error = "Missing app_customer_id metadata" });

        var customer = await _db.Customers.FindAsync(customerId);
        if (customer is null)
            return NotFound(new { error = $"Customer {customerId} not found" });

        // Credit in the same transaction as recording the event for atomicity
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Convert from Stripe cents to decimal
            decimal creditAmount = paymentIntent.Amount / 100m;

            customer.Balance += creditAmount;

            _db.ProcessedStripeEvents.Add(new ProcessedStripeEvent
            {
                StripeEventId = stripeEvent.Id,
                ProcessedAt = System.DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw; // Let ASP.NET return 500 so Stripe retries
        }

        return Ok();
    }
}

// Minimal EF Core entities — wire these up in your DbContext
public class Customer
{
    public string Id { get; set; } = default!;
    public decimal Balance { get; set; }
}

public class ProcessedStripeEvent
{
    public int Id { get; set; }
    public string StripeEventId { get; set; } = default!;
    public System.DateTime ProcessedAt { get; set; }
}

// Minimal DbContext stub showing required DbSets
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents => Set<ProcessedStripeEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<ProcessedStripeEvent>()
            .HasIndex(e => e.StripeEventId)
            .IsUnique(); // DB-level guard against duplicate processing
    }
}

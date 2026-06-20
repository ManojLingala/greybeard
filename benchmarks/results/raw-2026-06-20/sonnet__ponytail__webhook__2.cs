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
        _webhookSecret = config["Stripe:WebhookSecret"]
            ?? throw new InvalidOperationException("Stripe:WebhookSecret is not configured.");
    }

    [HttpPost]
    public async Task<IActionResult> Handle()
    {
        // Read raw body for signature verification — must happen before any middleware reads it
        string json;
        using (var reader = new StreamReader(Request.Body))
            json = await reader.ReadToEndAsync();

        // Verify the webhook signature using Stripe's library
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _webhookSecret,
                throwOnApiVersionMismatch: false
            );
        }
        catch (StripeException ex)
        {
            // Signature invalid — reject immediately
            return BadRequest(new { error = ex.Message });
        }

        // Only handle payment_intent.succeeded (idempotently)
        if (stripeEvent.Type == Events.PaymentIntentSucceeded)
        {
            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent is null)
                return BadRequest(new { error = "Could not deserialize PaymentIntent." });

            await CreditCustomerAsync(stripeEvent.Id, paymentIntent);
        }

        // Return 200 for all event types so Stripe stops retrying
        return Ok();
    }

    private async Task CreditCustomerAsync(string stripeEventId, PaymentIntent paymentIntent)
    {
        // Idempotency: skip if we already processed this event
        bool alreadyProcessed = await _db.ProcessedWebhookEvents
            .AnyAsync(e => e.StripeEventId == stripeEventId);

        if (alreadyProcessed)
            return;

        // Look up the internal customer via Stripe's customer ID stored at charge time
        string? stripeCustomerId = paymentIntent.CustomerId;
        if (string.IsNullOrEmpty(stripeCustomerId))
            return;

        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.StripeCustomerId == stripeCustomerId);

        if (customer is null)
            return;

        // Amount is in the currency's smallest unit (e.g. cents for USD)
        long amountReceived = paymentIntent.AmountReceived;

        // Credit the account and record the event atomically
        customer.CreditsInCents += amountReceived;

        _db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            StripeEventId = stripeEventId,
            ProcessedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
    }
}

// Minimal EF Core entity stubs — replace with your actual models
public class Customer
{
    public int Id { get; set; }
    public string StripeCustomerId { get; set; } = default!;
    public long CreditsInCents { get; set; }
}

public class ProcessedWebhookEvent
{
    public int Id { get; set; }
    public string StripeEventId { get; set; } = default!;
    public DateTime ProcessedAt { get; set; }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedWebhookEvent>()
            .HasIndex(e => e.StripeEventId)
            .IsUnique();
    }
}

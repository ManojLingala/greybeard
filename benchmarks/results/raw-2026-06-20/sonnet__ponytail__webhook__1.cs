using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;

[ApiController]
[Route("webhooks/stripe")]
public class StripeWebhookController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly string _webhookSecret;

    public StripeWebhookController(
        AppDbContext db,
        ILogger<StripeWebhookController> logger,
        IConfiguration config)
    {
        _db = db;
        _logger = logger;
        _webhookSecret = config["Stripe:WebhookSecret"]
            ?? throw new InvalidOperationException("Stripe:WebhookSecret not configured");
    }

    [HttpPost]
    public async Task<IActionResult> Handle()
    {
        // Read raw body for signature verification — must happen before any JSON binding
        string json;
        using (var reader = new StreamReader(Request.Body))
            json = await reader.ReadToEndAsync();

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _webhookSecret);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Stripe webhook signature validation failed");
            return BadRequest("Invalid signature");
        }

        // Only handle the event types we care about; ignore the rest with 200 OK
        if (stripeEvent.Type == Events.PaymentIntentSucceeded)
        {
            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent is null)
                return BadRequest("Unexpected event data type");

            await CreditCustomerIdempotentAsync(stripeEvent.Id, paymentIntent);
        }

        // Returning 2xx tells Stripe not to retry; any non-2xx will trigger redelivery
        return Ok();
    }

    private async Task CreditCustomerIdempotentAsync(string eventId, PaymentIntent paymentIntent)
    {
        // Idempotency: skip if we already processed this Stripe event
        bool alreadyProcessed = await _db.ProcessedStripeEvents
            .AnyAsync(e => e.StripeEventId == eventId);

        if (alreadyProcessed)
        {
            _logger.LogInformation("Stripe event {EventId} already processed, skipping", eventId);
            return;
        }

        string? customerId = paymentIntent.CustomerId;
        if (string.IsNullOrEmpty(customerId))
        {
            _logger.LogWarning("PaymentIntent {PaymentIntentId} has no CustomerId", paymentIntent.Id);
            return;
        }

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.StripeCustomerId == customerId);
        if (customer is null)
        {
            _logger.LogWarning("No local customer found for Stripe customer {CustomerId}", customerId);
            return;
        }

        // Amount is in smallest currency unit (e.g. cents); convert as appropriate for your domain
        long amountCents = paymentIntent.Amount;

        // Use a transaction so the credit and the idempotency record are committed atomically
        await using var tx = await _db.Database.BeginTransactionAsync();

        customer.CreditBalanceCents += amountCents;

        _db.ProcessedStripeEvents.Add(new ProcessedStripeEvent
        {
            StripeEventId = eventId,
            ProcessedAt = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        _logger.LogInformation(
            "Credited customer {CustomerId} with {AmountCents} cents (event {EventId})",
            customerId, amountCents, eventId);
    }
}

// Minimal entity models — replace with your real EF Core models
public class Customer
{
    public int Id { get; set; }
    public string StripeCustomerId { get; set; } = string.Empty;
    public long CreditBalanceCents { get; set; }
}

public class ProcessedStripeEvent
{
    public int Id { get; set; }
    public string StripeEventId { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents => Set<ProcessedStripeEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Unique index ensures the idempotency check is race-condition-safe at the DB level
        modelBuilder.Entity<ProcessedStripeEvent>()
            .HasIndex(e => e.StripeEventId)
            .IsUnique();

        modelBuilder.Entity<Customer>()
            .HasIndex(c => c.StripeCustomerId)
            .IsUnique();
    }
}

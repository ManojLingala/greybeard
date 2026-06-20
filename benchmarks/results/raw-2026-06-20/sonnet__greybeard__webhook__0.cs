// greybeard rungs that apply:
// 2. Mutation      -> idempotency key (Stripe event ID). Exactly-once credit, at-least-once delivery.
// 3. External call -> Stripe signature verification (trust-boundary). No outbound call needed here, but DB has timeout.
// 4. Concurrency   -> explicit transaction + row-level lock to prevent double-credit on concurrent redeliveries.
// 6. Partial fail  -> if DB commit fails after acking HTTP 200, Stripe redelivers; idempotency guard makes retry safe.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Payments.Controllers
{
    [ApiController]
    [Route("webhooks/stripe")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ILogger<StripeWebhookController> _logger;
        private readonly StripeWebhookOptions _options;

        // greybeard: no secrets in ctor args that end up in DI logs; options object is sealed.
        public StripeWebhookController(
            AppDbContext db,
            ILogger<StripeWebhookController> logger,
            Microsoft.Extensions.Options.IOptions<StripeWebhookOptions> options)
        {
            _db = db;
            _logger = logger;
            _options = options.Value;
        }

        [HttpPost]
        [Consumes("application/json")]
        public async Task<IActionResult> Handle(CancellationToken ct)
        {
            // greybeard: trust-boundary — read raw body before any model binding so HMAC covers bytes-on-wire.
            string json;
            using (var reader = new StreamReader(Request.Body))
            {
                json = await reader.ReadToEndAsync();
            }

            // greybeard: trust-boundary — reject anything that fails Stripe's HMAC signature check.
            //            Never trust the payload until the signature is verified.
            if (!Request.Headers.TryGetValue("Stripe-Signature", out var sigHeader))
            {
                _logger.LogWarning("Stripe webhook received without signature header");
                return Unauthorized();
            }

            Event stripeEvent;
            try
            {
                // greybeard: tolerance window is Stripe's default 300 s; replay attacks outside that window are rejected.
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    sigHeader,
                    _options.WebhookSecret,   // greybeard: secret from config, never hard-coded or logged.
                    throwOnApiVersionMismatch: false);
            }
            catch (StripeException ex)
            {
                // greybeard: no secret material in log — exception message from Stripe is safe (it's our own key check).
                _logger.LogWarning("Stripe signature validation failed: {Reason}", ex.Message);
                return Unauthorized();
            }

            // Only handle the events we care about; return 200 for others so Stripe stops retrying them.
            if (stripeEvent.Type != Events.PaymentIntentSucceeded)
            {
                return Ok(new { ignored = true });
            }

            if (stripeEvent.Data.Object is not PaymentIntent paymentIntent)
            {
                _logger.LogError("payment_intent.succeeded event {EventId} had unexpected data object type", stripeEvent.Id);
                // greybeard: return 400 so Stripe will retry — we want to see this event again until we can parse it.
                return BadRequest("Unexpected data object type");
            }

            // greybeard: money — amount is in minor units (cents) as an integer; never convert to float.
            long amountMinorUnits = paymentIntent.Amount;         // e.g. 1099 = $10.99 USD
            string currency = paymentIntent.Currency.ToUpperInvariant(); // greybeard: currency code travels with amount.

            string? customerId = paymentIntent.CustomerId;
            if (string.IsNullOrWhiteSpace(customerId))
            {
                _logger.LogError("payment_intent.succeeded event {EventId} has no CustomerId", stripeEvent.Id);
                return UnprocessableEntity("Missing customer");
            }

            // greybeard: idempotency — use Stripe event ID as the idempotency key.
            //            A re-delivered webhook with the same event ID must not double-credit.
            //            We write a ProcessedWebhookEvent row inside the same transaction as the credit.
            await using var tx = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct);

            try
            {
                // greybeard: concurrency — check for duplicate inside the transaction; use a unique index
                //            on ProcessedWebhookEvents.EventId (enforced at DB level) as the final guard.
                bool alreadyProcessed = await _db.ProcessedWebhookEvents
                    .AnyAsync(e => e.EventId == stripeEvent.Id, ct);

                if (alreadyProcessed)
                {
                    // greybeard: idempotency — safe no-op for redelivery; return 200 so Stripe stops retrying.
                    _logger.LogInformation(
                        "Stripe event {EventId} already processed — skipping duplicate credit", stripeEvent.Id);
                    await tx.RollbackAsync(ct);
                    return Ok(new { duplicate = true });
                }

                // greybeard: concurrency — row-level lock on the CustomerAccount row prevents two concurrent
                //            redeliveries from both reading balance=0 and each crediting independently.
                var account = await _db.CustomerAccounts
                    .FromSqlRaw(
                        "SELECT * FROM \"CustomerAccounts\" WHERE \"CustomerId\" = {0} FOR UPDATE",
                        customerId)
                    .FirstOrDefaultAsync(ct);

                if (account is null)
                {
                    _logger.LogError(
                        "No CustomerAccount found for CustomerId {CustomerId} (event {EventId})",
                        customerId, stripeEvent.Id);
                    await tx.RollbackAsync(ct);
                    // greybeard: partial fail — returning 422 causes Stripe to stop retrying; alert ops manually.
                    //            If this customer should exist, this is a data-integrity bug that needs human review.
                    return UnprocessableEntity("Customer account not found");
                }

                // greybeard: money — integer arithmetic only; no float, no decimal rounding surprises.
                account.BalanceMinorUnits += amountMinorUnits;
                account.CurrencyCode = currency; // greybeard: validate currency matches account's currency in prod.

                // greybeard: idempotency — record the processed event atomically with the credit.
                //            If the commit fails, both the credit and the event record roll back together;
                //            Stripe redelivers; we re-enter this handler and try again safely.
                _db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
                {
                    EventId = stripeEvent.Id,
                    EventType = stripeEvent.Type,
                    CustomerId = customerId,
                    AmountMinorUnits = amountMinorUnits,
                    Currency = currency,
                    ProcessedAt = DateTimeOffset.UtcNow,
                });

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                // greybeard: observable — structured log with no PII beyond customer ID, no card data, no secrets.
                _logger.LogInformation(
                    "Credited {Amount} {Currency} to customer {CustomerId} for Stripe event {EventId}",
                    amountMinorUnits, currency, customerId, stripeEvent.Id);

                return Ok(new { credited = true });
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // greybeard: concurrency — two concurrent redeliveries both pass the AnyAsync check (race),
                //            but only one wins the unique-index insert; the loser lands here. Safe no-op.
                await tx.RollbackAsync(ct);
                _logger.LogWarning(
                    "Concurrent duplicate detected for Stripe event {EventId} — unique constraint won", stripeEvent.Id);
                return Ok(new { duplicate = true });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                // greybeard: no secret/card data in log; exception message from EF is safe here.
                _logger.LogError(ex, "Failed to process Stripe event {EventId}", stripeEvent.Id);
                // greybeard: return 500 so Stripe retries; transaction rolled back so credit never happened.
                return StatusCode(500, "Internal error — will retry");
            }
        }

        // greybeard: unique-constraint detection is DB-driver-specific; centralised here so it's easy to adapt.
        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            // Works for Npgsql (PostgreSQL). Adapt for SqlException error 2627 on SQL Server, etc.
            return ex.InnerException?.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) == true
                || ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    // -------------------------------------------------------------------------
    // Supporting types (normally in separate files)
    // -------------------------------------------------------------------------

    public class StripeWebhookOptions
    {
        // greybeard: bound from configuration (e.g. environment variable STRIPE__WEBHOOKSECRET);
        //            never default to a known value; fail fast on startup if missing.
        public string WebhookSecret { get; set; } = string.Empty;
    }

    public class CustomerAccount
    {
        public int Id { get; set; }
        public string CustomerId { get; set; } = string.Empty;

        // greybeard: money — balance stored as integer minor units (e.g. cents). Never float or decimal with silent rounding.
        public long BalanceMinorUnits { get; set; }
        public string CurrencyCode { get; set; } = string.Empty;
    }

    public class ProcessedWebhookEvent
    {
        public int Id { get; set; }

        // greybeard: idempotency — unique index on EventId enforced at DB level (migration not shown).
        public string EventId { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public long AmountMinorUnits { get; set; }
        public string Currency { get; set; } = string.Empty;
        public DateTimeOffset ProcessedAt { get; set; }
    }

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<CustomerAccount> CustomerAccounts => Set<CustomerAccount>();
        public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // greybeard: idempotency — database-enforced uniqueness is the final defence against double-credit.
            modelBuilder.Entity<ProcessedWebhookEvent>()
                .HasIndex(e => e.EventId)
                .IsUnique();

            modelBuilder.Entity<CustomerAccount>()
                .HasIndex(a => a.CustomerId)
                .IsUnique();
        }
    }
}

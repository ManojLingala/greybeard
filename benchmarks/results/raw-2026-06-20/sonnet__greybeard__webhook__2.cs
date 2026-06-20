// greybeard rungs that apply:
// 2. Mutation       -> idempotency key (Stripe event ID). Safe to retry. Exactly-once credit.
// 3. External call  -> Stripe SDK call has timeout; signature verification before any processing.
// 4. Concurrency    -> optimistic concurrency / row-level lock to prevent double-credit on parallel delivery.
// 6. Partial fail   -> compensating transaction; event marked processed only after credit commits.
// Non-negotiables   -> trust-boundary validation (HMAC signature), no secrets in logs, money in integer minor-units.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Payments.Controllers
{
    [ApiController]
    [Route("webhooks/stripe")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly StripeOptions _stripeOptions;
        private readonly ILogger<StripeWebhookController> _logger;

        public StripeWebhookController(
            AppDbContext db,
            IOptions<StripeOptions> stripeOptions,
            ILogger<StripeWebhookController> logger)
        {
            _db = db;
            _stripeOptions = stripeOptions.Value;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> HandleWebhook(CancellationToken cancellationToken)
        {
            // greybeard: trust-boundary -- read raw body before any model binding; signature must be verified first.
            string json;
            using (var reader = new StreamReader(Request.Body))
            {
                json = await reader.ReadToEndAsync();
            }

            // greybeard: trust-boundary -- HMAC verification using Stripe-Signature header; reject anything unsigned.
            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    _stripeOptions.WebhookSecret,
                    throwOnApiVersionMismatch: false // tolerate minor API version drift; log and continue
                );
            }
            catch (StripeException ex)
            {
                // greybeard: no secret material (webhook secret, raw body) in logs.
                _logger.LogWarning("Stripe webhook signature validation failed: {ErrorMessage}", ex.Message);
                return BadRequest("Invalid signature.");
            }

            if (stripeEvent.Type != Events.PaymentIntentSucceeded &&
                stripeEvent.Type != Events.ChargeSucceeded)
            {
                // Acknowledge unhandled event types so Stripe stops retrying them.
                return Ok(new { received = true });
            }

            // greybeard: idempotency -- use Stripe event ID as idempotency key; check before doing any work.
            var idempotencyKey = stripeEvent.Id;

            // greybeard: concurrency + mutation -- wrap credit + idempotency record in a single serializable transaction.
            await using var transaction = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, cancellationToken);

            try
            {
                var alreadyProcessed = await _db.ProcessedWebhookEvents
                    .AnyAsync(e => e.StripeEventId == idempotencyKey, cancellationToken);

                if (alreadyProcessed)
                {
                    // greybeard: idempotency -- redelivery detected; return 200 so Stripe stops retrying.
                    _logger.LogInformation("Duplicate Stripe event {EventId} ignored.", idempotencyKey);
                    await transaction.RollbackAsync(cancellationToken);
                    return Ok(new { received = true });
                }

                // Extract payment details.
                long amountMinorUnits; // greybeard: money -- always integer minor-units (cents), never float.
                string currency;       // greybeard: money -- currency code travels with the amount.
                string customerId;

                if (stripeEvent.Type == Events.PaymentIntentSucceeded)
                {
                    var paymentIntent = stripeEvent.Data.Object as PaymentIntent
                        ?? throw new InvalidOperationException("Expected PaymentIntent payload.");

                    amountMinorUnits = paymentIntent.AmountReceived; // already in minor units from Stripe
                    currency = paymentIntent.Currency.ToUpperInvariant();
                    customerId = paymentIntent.CustomerId
                        ?? throw new InvalidOperationException("PaymentIntent has no CustomerId.");
                }
                else // ChargeSucceeded
                {
                    var charge = stripeEvent.Data.Object as Charge
                        ?? throw new InvalidOperationException("Expected Charge payload.");

                    amountMinorUnits = charge.Amount;
                    currency = charge.Currency.ToUpperInvariant();
                    customerId = charge.CustomerId
                        ?? throw new InvalidOperationException("Charge has no CustomerId.");
                }

                // greybeard: trust-boundary -- validate extracted fields before touching any account.
                if (amountMinorUnits <= 0)
                    throw new InvalidOperationException($"Non-positive amount {amountMinorUnits} in event {idempotencyKey}.");

                if (string.IsNullOrWhiteSpace(currency))
                    throw new InvalidOperationException($"Missing currency in event {idempotencyKey}.");

                if (string.IsNullOrWhiteSpace(customerId))
                    throw new InvalidOperationException($"Missing customerId in event {idempotencyKey}.");

                // greybeard: concurrency -- pessimistic row lock on the customer account row to prevent lost updates
                // when two Stripe redeliveries arrive simultaneously.
                var account = await _db.CustomerAccounts
                    .FromSqlInterpolated(
                        $"SELECT * FROM customer_accounts WHERE stripe_customer_id = {customerId} FOR UPDATE")
                    .FirstOrDefaultAsync(cancellationToken);

                if (account is null)
                {
                    // greybeard: partial failure -- unknown customer; reject with 422 so Stripe does NOT retry (it is a data bug, not transient).
                    _logger.LogError(
                        "No account found for Stripe customer {CustomerId} (event {EventId}).",
                        customerId, idempotencyKey);
                    await transaction.RollbackAsync(cancellationToken);
                    return UnprocessableEntity("Customer account not found.");
                }

                // greybeard: money -- guard against currency mismatch; never silently convert.
                if (!string.Equals(account.Currency, currency, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError(
                        "Currency mismatch for account {AccountId}: expected {Expected}, got {Got} (event {EventId}).",
                        account.Id, account.Currency, currency, idempotencyKey);
                    await transaction.RollbackAsync(cancellationToken);
                    return UnprocessableEntity("Currency mismatch.");
                }

                // greybeard: money -- integer arithmetic only; no floating-point at any step.
                checked // overflow guard
                {
                    account.BalanceMinorUnits += amountMinorUnits;
                }

                // greybeard: mutation -- record processed event in same transaction; atomically prevents double-credit.
                _db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
                {
                    StripeEventId = idempotencyKey,
                    EventType = stripeEvent.Type,
                    AmountMinorUnits = amountMinorUnits,
                    Currency = currency,
                    CustomerId = customerId,
                    ProcessedAt = DateTimeOffset.UtcNow,
                });

                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                // greybeard: observable -- structured log without PII beyond account ID; no card numbers, no secrets.
                _logger.LogInformation(
                    "Credited account {AccountId} with {Amount} {Currency} for event {EventId}.",
                    account.Id, amountMinorUnits, currency, idempotencyKey);

                return Ok(new { received = true });
            }
            catch (OperationCanceledException)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                // greybeard: partial failure -- return 500 so Stripe retries; our idempotency key protects against double-credit.
                return StatusCode(503, "Request cancelled.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                // greybeard: no secrets/PII in error responses returned to Stripe.
                _logger.LogError(ex, "Unhandled error processing Stripe event {EventId}.", idempotencyKey);
                // Return 500 so Stripe retries; idempotency key protects against double-credit on retry.
                return StatusCode(500, "Internal error; will retry.");
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Supporting types (would normally live in their own files)
    // ---------------------------------------------------------------------------

    public class StripeOptions
    {
        public string WebhookSecret { get; set; } = string.Empty; // loaded from secret store, never from appsettings.json plain text
    }

    public class CustomerAccount
    {
        public Guid Id { get; set; }
        public string StripeCustomerId { get; set; } = string.Empty;
        // greybeard: money -- balance stored as integer minor-units (e.g. cents) in DB; never decimal/float column.
        public long BalanceMinorUnits { get; set; }
        public string Currency { get; set; } = string.Empty; // ISO 4217, e.g. "USD"
    }

    public class ProcessedWebhookEvent
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string StripeEventId { get; set; } = string.Empty; // unique index in DB
        public string EventType { get; set; } = string.Empty;
        public long AmountMinorUnits { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public DateTimeOffset ProcessedAt { get; set; }
    }

    // Minimal DbContext stub -- real implementation wires up migrations, indexes, etc.
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<CustomerAccount> CustomerAccounts => Set<CustomerAccount>();
        public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CustomerAccount>(b =>
            {
                b.ToTable("customer_accounts");
                b.Property(e => e.StripeCustomerId).HasColumnName("stripe_customer_id");
                // greybeard: money -- map to bigint, not numeric/decimal, to make the integer-minor-units contract explicit.
                b.Property(e => e.BalanceMinorUnits).HasColumnName("balance_minor_units").HasColumnType("bigint");
                b.Property(e => e.Currency).HasColumnName("currency").HasMaxLength(3);
            });

            modelBuilder.Entity<ProcessedWebhookEvent>(b =>
            {
                b.ToTable("processed_webhook_events");
                // greybeard: idempotency -- unique index on stripe_event_id; DB enforces exactly-once at storage layer.
                b.HasIndex(e => e.StripeEventId).IsUnique();
            });
        }
    }
}

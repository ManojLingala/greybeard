// greybeard: Rungs that apply here:
//   2. Mutation    -> idempotency key (Stripe event ID). Exactly-once credit, at-least-once delivery.
//   3. External call -> Stripe signature verification; no outbound call needed but we guard the inbound trust boundary.
//   4. Concurrency -> row-level lock + unique constraint on processed event ID to prevent double-credit.
//   6. Half-way failure -> outbox / transactional write so credit and event-log commit together or not at all.
// Non-negotiables: trust-boundary validation on signature; no secrets in logs; money in minor-units (cents).

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
        private readonly ILogger<StripeWebhookController> _logger;
        private readonly StripeOptions _options;

        public StripeWebhookController(
            AppDbContext db,
            ILogger<StripeWebhookController> logger,
            IOptions<StripeOptions> options)
        {
            _db = db;
            _logger = logger;
            _options = options.Value;
        }

        [HttpPost]
        // greybeard: trust boundary — consume raw body BEFORE any model binding touches it;
        //            signature verification uses the exact bytes Stripe signed.
        [Consumes("application/json")]
        public async Task<IActionResult> Handle(CancellationToken ct)
        {
            string json;
            using (var reader = new StreamReader(HttpContext.Request.Body))
                json = await reader.ReadToEndAsync();

            // greybeard: trust boundary — reject anything that fails HMAC-SHA256 sig check.
            //            ConstructEvent throws StripeException on tampered/replayed payloads.
            Event stripeEvent;
            try
            {
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    _options.WebhookSecret,   // greybeard: secret from config, never logged.
                    throwOnApiVersionMismatch: false);
            }
            catch (StripeException ex)
            {
                // greybeard: no secret material or raw payload in the log.
                _logger.LogWarning("Stripe signature verification failed: {Message}", ex.Message);
                return BadRequest("Invalid signature.");
            }

            // We only process payment_intent.succeeded; ack everything else so Stripe stops retrying.
            if (stripeEvent.Type != Events.PaymentIntentSucceeded)
                return Ok();

            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent is null)
            {
                _logger.LogError("Event {EventId} type {Type}: data object is not a PaymentIntent.", stripeEvent.Id, stripeEvent.Type);
                return Ok(); // ack so Stripe does not retry an event we cannot parse
            }

            // greybeard: money in minor-units (cents) straight from Stripe — never float.
            //            Amount is already an integer (long) in the Stripe SDK.
            long creditAmountMinorUnits = paymentIntent.Amount; // e.g. 1999 = $19.99 USD
            string currency = paymentIntent.Currency.ToUpperInvariant(); // travels with the amount

            if (string.IsNullOrWhiteSpace(paymentIntent.CustomerId))
            {
                _logger.LogError("Event {EventId}: PaymentIntent has no CustomerId; cannot credit account.", stripeEvent.Id);
                return Ok();
            }

            string customerId = paymentIntent.CustomerId;
            string idempotencyKey = stripeEvent.Id; // greybeard: Stripe event ID is the natural idempotency key for redelivery.

            try
            {
                await CreditAccountExactlyOnceAsync(idempotencyKey, customerId, creditAmountMinorUnits, currency, ct);
            }
            catch (OperationCanceledException)
            {
                // greybeard: request cancelled (client disconnect / shutdown); return 500 so Stripe retries.
                return StatusCode(503);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing event {EventId}.", stripeEvent.Id);
                // greybeard: return 500 to trigger Stripe retry; do NOT expose internal detail.
                return StatusCode(500);
            }

            return Ok();
        }

        // greybeard: Mutation — the entire credit+event-log write happens in ONE database transaction
        //            so either both commit or neither does (half-way failure protection).
        //            Unique index on ProcessedStripeEvents.EventId is the last line of defense against
        //            a double-credit if two concurrent deliveries race past the SELECT check.
        private async Task CreditAccountExactlyOnceAsync(
            string idempotencyKey,
            string customerId,
            long amountMinorUnits,
            string currency,
            CancellationToken ct)
        {
            // greybeard: concurrency — serializable or RepeatableRead isolation prevents phantom read;
            //            unique DB constraint on event_id is the hard stop.
            await using var tx = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead, ct);

            // greybeard: idempotency — check whether we already processed this Stripe event.
            bool alreadyProcessed = await _db.ProcessedStripeEvents
                .AnyAsync(e => e.EventId == idempotencyKey, ct);

            if (alreadyProcessed)
            {
                _logger.LogInformation("Event {EventId} already processed; skipping (idempotent).", idempotencyKey);
                await tx.RollbackAsync(ct);
                return;
            }

            // greybeard: concurrency — lock the customer row to prevent a concurrent webhook
            //            from crediting simultaneously and creating a lost update.
            var account = await _db.CustomerAccounts
                .FromSqlRaw("SELECT * FROM customer_accounts WHERE customer_id = {0} FOR UPDATE", customerId)
                .SingleOrDefaultAsync(ct);

            if (account is null)
            {
                // greybeard: half-way failure — log and ack so Stripe stops retrying an event
                //            we genuinely cannot fulfil; alert on-call instead.
                _logger.LogError("Event {EventId}: no account found for customer {CustomerId}.", idempotencyKey, customerId);
                await tx.RollbackAsync(ct);
                return; // still return 200 to caller so Stripe stops retrying; on-call alert handles it
            }

            // greybeard: money — add integer minor-units; no float arithmetic, no silent rounding.
            if (account.Currency != currency)
            {
                _logger.LogError(
                    "Event {EventId}: currency mismatch; account={AccountCurrency} payment={PaymentCurrency}.",
                    idempotencyKey, account.Currency, currency);
                await tx.RollbackAsync(ct);
                return;
            }

            account.BalanceMinorUnits += amountMinorUnits;

            // greybeard: mutation — record processed event atomically with the balance update.
            _db.ProcessedStripeEvents.Add(new ProcessedStripeEvent
            {
                EventId = idempotencyKey,
                CustomerId = customerId,
                AmountMinorUnits = amountMinorUnits,
                Currency = currency,
                ProcessedAtUtc = DateTime.UtcNow
            });

            // greybeard: observable — structured log with correlation fields; no PII/secrets.
            _logger.LogInformation(
                "Crediting account for customer {CustomerId}: +{Amount} {Currency} (event {EventId}).",
                customerId, amountMinorUnits, currency, idempotencyKey);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            // greybeard: if SaveChanges or Commit throws a unique-constraint violation
            //            (race with a concurrent delivery), EF/Npgsql surfaces a DbUpdateException;
            //            the outer handler returns 500, Stripe retries, the second pass hits the
            //            alreadyProcessed guard and returns 200. Safe.
        }
    }

    // ---- Supporting types (would normally live in separate files) ----

    public class StripeOptions
    {
        // greybeard: loaded from IConfiguration/secrets manager, never hardcoded or logged.
        public string WebhookSecret { get; set; } = string.Empty;
    }

    public class CustomerAccount
    {
        public string CustomerId { get; set; } = string.Empty;
        // greybeard: money — balance stored as integer minor-units (e.g. cents), never decimal/float.
        public long BalanceMinorUnits { get; set; }
        public string Currency { get; set; } = string.Empty; // ISO 4217, e.g. "USD"
        public int RowVersion { get; set; } // for optimistic concurrency on non-PostgreSQL stores
    }

    public class ProcessedStripeEvent
    {
        public int Id { get; set; }
        // greybeard: unique index on EventId is the DB-level idempotency guard (DDL below).
        public string EventId { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public long AmountMinorUnits { get; set; }
        public string Currency { get; set; } = string.Empty;
        public DateTime ProcessedAtUtc { get; set; }
    }

    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<CustomerAccount> CustomerAccounts => Set<CustomerAccount>();
        public DbSet<ProcessedStripeEvent> ProcessedStripeEvents => Set<ProcessedStripeEvent>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<CustomerAccount>(e =>
            {
                e.HasKey(a => a.CustomerId);
                e.ToTable("customer_accounts");
                e.Property(a => a.BalanceMinorUnits).HasColumnName("balance_minor_units");
                e.Property(a => a.Currency).HasMaxLength(3);
                // greybeard: concurrency — row-version token; EF throws DbUpdateConcurrencyException on stale write.
                e.Property(a => a.RowVersion).IsRowVersion();
            });

            mb.Entity<ProcessedStripeEvent>(e =>
            {
                e.HasKey(ev => ev.Id);
                e.ToTable("processed_stripe_events");
                // greybeard: idempotency — unique constraint ensures duplicate event IDs fail at the DB level
                //            even if two processes race past the application-level check.
                e.HasIndex(ev => ev.EventId).IsUnique();
                e.Property(ev => ev.EventId).HasMaxLength(255);
                e.Property(ev => ev.Currency).HasMaxLength(3);
            });
        }
    }
}

/*
greybeard 3am review:

WHAT STILL NEEDS ATTENTION:
1. The SELECT ... FOR UPDATE raw SQL works on PostgreSQL/MySQL but not SQL Server
   (use UPDLOCK hint instead). Make this dialect-aware or use app-level distributed lock.
2. BalanceMinorUnits overflow: long holds ~9.2e18 cents — practically safe, but assert
   account.BalanceMinorUnits + amountMinorUnits does not overflow if paranoia demands it.
3. Alert on the "no account found" and "currency mismatch" branches — returning 200 silences
   Stripe but a human must investigate; hook these log lines to PagerDuty / Sentry.
4. WebhookSecret rotation: Stripe supports two active secrets during rotation; consider
   trying both secrets before rejecting the request.
5. Rate / replay window: Stripe replays for up to 72h. The processed_stripe_events table
   must be retained for at least that window before archiving old rows.
6. Idempotency key alone is not enough if you ever refund and re-process; events have
   distinct IDs so this is fine, but document the assumption.
*/

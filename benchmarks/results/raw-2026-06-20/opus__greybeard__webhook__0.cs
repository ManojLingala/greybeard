using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

// greybeard: rungs in play -> 1 Money (minor-units), 2 Mutation (idempotency on event id),
// 3 External call (signature is the trust boundary; DB ops time-bounded),
// 4 Concurrency (txn + row lock, no double-credit under redelivery),
// 6 Half-way (event-record + credit in one txn so partial credit is impossible),
// 7 minimum correct + observable, no secrets.

namespace Payments.Webhooks
{
    public sealed class StripeWebhookOptions
    {
        // greybeard: secret is configuration, never logged, never echoed in errors.
        public string SigningSecret { get; set; } = string.Empty;
    }

    [ApiController]
    [Route("webhooks/stripe")]
    public sealed class StripeWebhookController : ControllerBase
    {
        private readonly BillingDbContext _db;
        private readonly string _signingSecret;
        private readonly ILogger<StripeWebhookController> _logger;

        // greybeard: Stripe's own tolerance for the signed-timestamp check (replay window). 5 min is Stripe's default.
        private static readonly long SignatureToleranceSeconds = 300;

        public StripeWebhookController(
            BillingDbContext db,
            IOptions<StripeWebhookOptions> options,
            ILogger<StripeWebhookController> logger)
        {
            _db = db;
            _signingSecret = options.Value.SigningSecret;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> Handle(CancellationToken cancellationToken)
        {
            // greybeard: rung 3 trust boundary -> read the RAW body. Signature is computed over exact bytes;
            // model binding or re-serialization would invalidate it. Never trust the payload before verifying.
            string payload;
            using (var reader = new StreamReader(Request.Body))
            {
                payload = await reader.ReadToEndAsync();
            }

            var signatureHeader = Request.Headers["Stripe-Signature"];

            Event stripeEvent;
            try
            {
                // greybeard: rung 3 -> verify HMAC signature AND timestamp tolerance (replay defence) before
                // a single byte of the payload is trusted. throwOnApiVersionMismatch=false so a Stripe-side
                // API version bump doesn't drop legitimate events on the floor.
                stripeEvent = EventUtility.ConstructEvent(
                    payload,
                    signatureHeader,
                    _signingSecret,
                    tolerance: SignatureToleranceSeconds,
                    throwOnApiVersionMismatch: false);
            }
            catch (StripeException)
            {
                // greybeard: invalid/forged/expired signature. 400 so Stripe does NOT keep retrying garbage.
                // greybeard: no secret, no payload, no header echoed into the log.
                _logger.LogWarning("Rejected Stripe webhook: signature verification failed.");
                return BadRequest();
            }

            // greybeard: we only act on the one event we credit on. Everything else is acknowledged (200)
            // so Stripe stops retrying, but does nothing. No unbounded switch over event types we don't own.
            if (stripeEvent.Type != Events.PaymentIntentSucceeded)
            {
                _logger.LogInformation("Ignoring Stripe event {EventType} {EventId}.",
                    stripeEvent.Type, stripeEvent.Id);
                return Ok();
            }

            if (stripeEvent.Data.Object is not PaymentIntent intent)
            {
                // greybeard: shape we didn't expect. Ack so it isn't retried forever, but credit nothing.
                _logger.LogWarning("payment_intent.succeeded {EventId} had no PaymentIntent payload.",
                    stripeEvent.Id);
                return Ok();
            }

            // greybeard: rung 1 Money -> Stripe gives integer minor-units (e.g. cents) already. Keep them as long.
            // No float ever touches currency. Currency code travels WITH the amount end to end.
            long amountMinorUnits = intent.AmountReceived; // already minor units
            string currency = intent.Currency;              // ISO 4217, lowercase from Stripe

            // greybeard: rung 1 -> guard against a zero/negative or missing-currency credit slipping through.
            if (amountMinorUnits <= 0 || string.IsNullOrWhiteSpace(currency))
            {
                _logger.LogWarning("payment_intent.succeeded {EventId} had non-creditable amount/currency.",
                    stripeEvent.Id);
                return Ok();
            }

            // greybeard: trust-boundary validation -> the account to credit must be one we put on the intent
            // ourselves (metadata we control at PaymentIntent creation). Never derive identity from caller-supplied
            // free text we didn't set.
            if (intent.Metadata is null ||
                !intent.Metadata.TryGetValue("account_id", out var accountId) ||
                string.IsNullOrWhiteSpace(accountId))
            {
                _logger.LogWarning("payment_intent.succeeded {EventId} missing account_id metadata.",
                    stripeEvent.Id);
                return Ok();
            }

            try
            {
                await CreditOnceAsync(stripeEvent.Id, accountId, amountMinorUnits, currency, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // greybeard: rung 6 -> we did NOT commit (transaction rolls back). Return 5xx so Stripe REDELIVERS;
                // idempotency below makes the redelivery safe. Better a retry than a half-applied credit.
                _logger.LogWarning("Crediting {EventId} was cancelled/timed out before commit; will be retried.",
                    stripeEvent.Id);
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                // greybeard: transaction rolled back -> no credit, no event row. 5xx => Stripe retries. Safe.
                _logger.LogError(ex, "Crediting {EventId} failed before commit; will be retried.", stripeEvent.Id);
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            // greybeard: rung 7 observability -> success path is auditable by event id + account, amount kept in minor units.
            _logger.LogInformation(
                "Credited account {AccountId} {Amount} {Currency} for event {EventId}.",
                accountId, amountMinorUnits, currency, stripeEvent.Id);

            return Ok();
        }

        /// <summary>
        /// greybeard: rungs 2 + 4 + 6 collapsed into ONE atomic unit.
        /// Exactly-once effect under at-least-once delivery: the Stripe event id is the idempotency key.
        /// </summary>
        private async Task CreditOnceAsync(
            string eventId,
            string accountId,
            long amountMinorUnits,
            string currency,
            CancellationToken cancellationToken)
        {
            // greybeard: rung 3 -> bound the DB work. A wedged row lock must not hold the request (and a Stripe
            // worker) open forever. Linked CTS gives us our own timeout on top of the request's cancellation.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var ct = linkedCts.Token;

            // greybeard: rung 4 -> explicit transaction boundary. The dedupe insert and the balance update either
            // both commit or both roll back. Serializable not required; the unique index + row lock below carry it.
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // greybeard: rung 2 idempotency -> ProcessedWebhookEvents.EventId is a PRIMARY/UNIQUE key.
            // First delivery inserts; a redelivery hits the unique constraint and we no-op. This is the
            // exactly-once gate. The DB, not application timing, is the source of truth for "already processed".
            var alreadyProcessed = await _db.ProcessedWebhookEvents
                .AsNoTracking()
                .AnyAsync(e => e.EventId == eventId, ct);

            if (alreadyProcessed)
            {
                // greybeard: redelivery of an event we already credited. Commit nothing, ack 200 upstream.
                _logger.LogInformation("Duplicate Stripe event {EventId}; already credited, skipping.", eventId);
                await tx.RollbackAsync(ct);
                return;
            }

            // greybeard: rung 4 -> row lock on the target account so two concurrent redeliveries of the same
            // event can't both pass the dedupe check and both credit. The second waits, then sees the
            // ProcessedWebhookEvents row this txn inserts and the unique-key violation aborts it.
            var account = await _db.Accounts
                .FromSqlInterpolated($"SELECT * FROM accounts WHERE id = {accountId} FOR UPDATE")
                .FirstOrDefaultAsync(ct);

            if (account is null)
            {
                // greybeard: data-loss safety -> we will NOT credit a balance we can't find. Record the event as
                // processed so Stripe stops retrying a credit that can never land, and alert for manual reconciliation.
                _logger.LogError("Account {AccountId} not found for event {EventId}; recording as handled for review.",
                    accountId, eventId);
            }
            else
            {
                // greybeard: rung 1 Money -> reject cross-currency credits rather than silently mixing minor units.
                if (!string.Equals(account.Currency, currency, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Currency mismatch crediting event {eventId}: account is {account.Currency}, payment is {currency}.");
                }

                // greybeard: rung 1 -> pure integer addition of minor units. No rounding, no float, no precision loss.
                account.BalanceMinorUnits = checked(account.BalanceMinorUnits + amountMinorUnits);
            }

            // greybeard: rung 2 -> the idempotency record is inserted INSIDE the same txn as the credit.
            // They commit together (rung 6: no half-way state). The unique key is the concurrency backstop.
            _db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
            {
                EventId = eventId,
                AccountId = accountId,
                AmountMinorUnits = amountMinorUnits,
                Currency = currency,
                ProcessedAtUtc = DateTime.UtcNow,
            });

            try
            {
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // greybeard: a concurrent delivery committed first and grabbed the event id. That delivery did the
                // credit; ours must not. Roll back -> exactly-once preserved under the race.
                _logger.LogInformation("Concurrent delivery already credited event {EventId}; rolling back.", eventId);
                await tx.RollbackAsync(CancellationToken.None);
            }
        }

        // greybeard: provider-specific unique-constraint detection. Wire to your driver (e.g. PostgresException 23505).
        private static bool IsUniqueViolation(DbUpdateException ex)
        {
            var message = ex.InnerException?.Message ?? string.Empty;
            return message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
                || message.Contains("23505"); // PostgreSQL unique_violation
        }
    }

    // --- supporting persistence shapes (greybeard: money is always integer minor-units + currency) ---

    public sealed class BillingDbContext : DbContext
    {
        public BillingDbContext(DbContextOptions<BillingDbContext> options) : base(options) { }

        public DbSet<Account> Accounts => Set<Account>();
        public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Account>(e =>
            {
                e.ToTable("accounts");
                e.HasKey(a => a.Id);
                e.Property(a => a.BalanceMinorUnits); // greybeard: bigint, never decimal-with-float-math
                e.Property(a => a.Currency).HasMaxLength(3);
            });

            b.Entity<ProcessedWebhookEvent>(e =>
            {
                e.ToTable("processed_webhook_events");
                // greybeard: rung 2 -> the idempotency key. Unique by construction (primary key).
                e.HasKey(p => p.EventId);
            });
        }
    }

    public sealed class Account
    {
        public string Id { get; set; } = string.Empty;
        public long BalanceMinorUnits { get; set; }   // greybeard: integer minor units only
        public string Currency { get; set; } = string.Empty;
    }

    public sealed class ProcessedWebhookEvent
    {
        public string EventId { get; set; } = string.Empty; // greybeard: Stripe event id == idempotency key
        public string AccountId { get; set; } = string.Empty;
        public long AmountMinorUnits { get; set; }
        public string Currency { get; set; } = string.Empty;
        public DateTime ProcessedAtUtc { get; set; }
    }
}

// greybeard 3am re-read: signature+timestamp verified before trust; raw body preserved; event id is a unique
// idempotency key so a redelivered or concurrently-delivered webhook credits exactly once; credit + dedupe row
// commit in one transaction under a row lock (no lost update, no half credit); money stays integer minor units
// with currency checked; 5xx on pre-commit failure asks Stripe to retry, 200 on dupes/ignored stops the storm;
// no secret or payload ever hits a log. Remaining flag: missing-account credits are recorded + alerted for
// manual reconciliation rather than silently dropped or infinitely retried.

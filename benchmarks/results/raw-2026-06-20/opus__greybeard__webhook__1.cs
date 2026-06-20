using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Payments.Webhooks;

// greybeard ladder for "Stripe payment_succeeded -> credit account":
//   1. Money?            YES -> credit in integer minor-units; currency travels with amount; reject mismatched currency.
//   2. Mutation?         YES -> Stripe event.id is the idempotency key; redelivery must credit exactly once.
//   3. External call?    Signature verify is local crypto (no network), so no timeout/retry needed here.
//   4. Concurrency?      YES -> two concurrent deliveries of the same event race the same row; unique index + tx + row lock.
//   5. Reads a list?     NO.
//   6. Fail half-way?    YES -> persist the processed-event record AND the credit in ONE transaction (atomic, recoverable).
//   7. Minimum correct code, made observable (structured logs keyed by event id; never the secret/PII).

[ApiController]
[Route("webhooks/stripe")]
public sealed class StripeWebhookController : ControllerBase
{
    private readonly BillingDbContext _db;
    private readonly ILogger<StripeWebhookController> _log;
    private readonly string _webhookSecret;

    public StripeWebhookController(
        BillingDbContext db,
        IOptions<StripeOptions> options,
        ILogger<StripeWebhookController> log)
    {
        _db = db;
        _log = log;
        // greybeard: secret comes from configuration/secret-store, never logged or echoed in error bodies.
        _webhookSecret = options.Value.WebhookSigningSecret;
    }

    [HttpPost]
    public async Task<IActionResult> Handle(CancellationToken ct)
    {
        // greybeard: trust boundary -- read the RAW body exactly as sent. Signature is computed over raw bytes;
        // any model-binding/re-serialization would break verification.
        string payload;
        using (var reader = new StreamReader(Request.Body))
        {
            payload = await reader.ReadToEndAsync(ct);
        }

        var signatureHeader = Request.Headers["Stripe-Signature"].ToString();

        Event stripeEvent;
        try
        {
            // greybeard: verify signature + timestamp tolerance (replay window). throwOnApiVersionMismatch guards
            // against a payload shape we did not expect. This is the trust boundary -- nothing below runs unverified.
            stripeEvent = EventUtility.ConstructEvent(
                payload,
                signatureHeader,
                _webhookSecret,
                throwOnApiVersionMismatch: true);
        }
        catch (StripeException)
        {
            // greybeard: bad/forged/expired signature -> 400, no detail leaked to the caller. Do not log the secret.
            _log.LogWarning("Rejected Stripe webhook: signature verification failed.");
            return BadRequest();
        }

        // greybeard: we only act on the event we were asked to handle; everything else is acked so Stripe stops retrying.
        if (stripeEvent.Type != EventTypes.PaymentIntentSucceeded)
        {
            return Ok();
        }

        if (stripeEvent.Data.Object is not PaymentIntent intent)
        {
            _log.LogWarning("payment_intent.succeeded event {EventId} had unexpected payload shape.", stripeEvent.Id);
            return BadRequest();
        }

        // greybeard: trust-boundary validation on the credit inputs before any money moves.
        var customerRef = intent.Metadata is not null && intent.Metadata.TryGetValue("account_id", out var acct)
            ? acct
            : intent.CustomerId;
        if (string.IsNullOrWhiteSpace(customerRef))
        {
            _log.LogWarning("payment_intent {PaymentIntentId} (event {EventId}) has no account reference.",
                intent.Id, stripeEvent.Id);
            return BadRequest();
        }

        // greybeard: MONEY. intent.Amount is already integer minor-units (long). No float, ever.
        // Reject non-positive amounts and carry the currency code with the amount.
        long amountMinor = intent.Amount;
        string currency = (intent.Currency ?? string.Empty).ToUpperInvariant();
        if (amountMinor <= 0 || currency.Length != 3)
        {
            _log.LogWarning("payment_intent {PaymentIntentId} (event {EventId}) has invalid amount/currency.",
                intent.Id, stripeEvent.Id);
            return BadRequest();
        }

        try
        {
            // greybeard: CONCURRENCY + HALF-WAY. Serializable so the dedup read and the credit write are one decision;
            // the unique index on ProcessedEvents.EventId is the real backstop against concurrent double-credit.
            await using var tx = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            // greybeard: IDEMPOTENCY. The Stripe event.id is globally unique and stable across redeliveries.
            // If we've already recorded it, this delivery is a retry -> ack with 200, do nothing.
            bool alreadyProcessed = await _db.ProcessedEvents
                .AsNoTracking()
                .AnyAsync(e => e.EventId == stripeEvent.Id, ct);
            if (alreadyProcessed)
            {
                _log.LogInformation("Stripe event {EventId} already processed; acking redelivery.", stripeEvent.Id);
                await tx.CommitAsync(ct);
                return Ok();
            }

            // greybeard: row lock the account so two distinct events for the same account can't lose-update the balance.
            var account = await _db.Accounts
                .FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\" = {customerRef} FOR UPDATE")
                .FirstOrDefaultAsync(ct);
            if (account is null)
            {
                _log.LogWarning("Account {AccountId} not found for event {EventId}.", customerRef, stripeEvent.Id);
                // greybeard: unknown account is a data problem, not a transient one -> ack so Stripe stops retrying,
                // and surface it for reconciliation rather than looping forever.
                await tx.CommitAsync(ct);
                return Ok();
            }

            // greybeard: MONEY. Currency must match the account's ledger currency; never silently convert.
            if (!string.Equals(account.Currency, currency, StringComparison.Ordinal))
            {
                _log.LogWarning("Currency mismatch for account {AccountId}: account={AccountCurrency} event={EventCurrency}.",
                    customerRef, account.Currency, currency);
                await tx.CommitAsync(ct);
                return Ok();
            }

            // greybeard: integer arithmetic on minor-units only. checked to fail loud on overflow rather than wrap.
            account.BalanceMinor = checked(account.BalanceMinor + amountMinor);

            // greybeard: record the event INSIDE the same tx. Either both the credit and the dedup marker land, or neither.
            _db.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = stripeEvent.Id,
                ProcessedAtUtc = DateTime.UtcNow,
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _log.LogInformation(
                "Credited account {AccountId} by {AmountMinor} {Currency} from event {EventId}.",
                customerRef, amountMinor, currency, stripeEvent.Id);

            return Ok();
        }
        catch (DbUpdateException) // unique-index violation = concurrent delivery won the race first.
        {
            // greybeard: the other delivery already credited and recorded the event. Exactly-once preserved; ack it.
            _log.LogInformation("Concurrent delivery already processed event {EventId}; acking.", stripeEvent.Id);
            return Ok();
        }
        catch (OperationCanceledException)
        {
            // greybeard: client/server aborted before commit -> nothing persisted. 503 tells Stripe to retry (at-least-once).
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            // greybeard: transient/unknown failure -> 500 so Stripe redelivers; redelivery is safe (idempotent above).
            _log.LogError(ex, "Failed to process Stripe event {EventId}.", stripeEvent.Id);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}

public sealed class StripeOptions
{
    public string WebhookSigningSecret { get; set; } = string.Empty;
}

// greybeard: ProcessedEvents.EventId MUST have a UNIQUE index -- it is the idempotency backstop, not a nicety.
//   migrationBuilder.CreateIndex("IX_ProcessedEvents_EventId", "ProcessedEvents", "EventId", unique: true);
public sealed class ProcessedEvent
{
    public long Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public DateTime ProcessedAtUtc { get; set; }
}

public sealed class Account
{
    public string Id { get; set; } = string.Empty;
    public long BalanceMinor { get; set; }   // greybeard: minor-units integer ledger; no decimal/float money fields.
    public string Currency { get; set; } = string.Empty; // ISO-4217, travels with the balance.
}

public sealed class BillingDbContext : DbContext
{
    public BillingDbContext(DbContextOptions<BillingDbContext> options) : base(options) { }

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessedEvent>()
            .HasIndex(e => e.EventId)
            .IsUnique(); // greybeard: enforces exactly-once even under concurrent redelivery.
    }
}

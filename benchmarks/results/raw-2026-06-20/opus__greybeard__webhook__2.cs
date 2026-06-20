// StripeWebhookController.cs
//
// Handles Stripe `payment_intent.succeeded` (a.k.a. payment_succeeded) and
// credits the customer's account ledger.
//
// greybeard ladder applied to this task:
//   1. Money?            YES -> amounts are integer minor-units (long), currency travels with them. No float, ever.
//   2. Mutation?         YES -> crediting is a state mutation. Idempotency keyed on Stripe event.id (exactly-once effect).
//   3. External call?    Verification is local (HMAC); the DB write is the durable boundary -> bounded, transactional.
//   4. Concurrency?      YES -> two redeliveries can race the same event. Unique constraint + serializable tx prevent double-credit.
//   5. Reads a list?     NO  -> single-event handler. Skipped.
//   6. Fail half-way?    YES -> persist event + credit in ONE transaction. Either both commit or neither does.
//   7. Observability     -> structured logs keyed on event.id; NEVER log signature, payload secrets, or PII.

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

[ApiController]
[Route("webhooks/stripe")]
public sealed class StripeWebhookController : ControllerBase
{
    private readonly LedgerDbContext _db;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly StripeWebhookOptions _options;

    public StripeWebhookController(
        LedgerDbContext db,
        ILogger<StripeWebhookController> logger,
        IOptions<StripeWebhookOptions> options)
    {
        _db = db;
        _logger = logger;
        _options = options.Value;
    }

    [HttpPost]
    public async Task<IActionResult> Handle(CancellationToken ct)
    {
        // greybeard: trust-boundary validation -- the request body is untrusted until the
        // Stripe-Signature HMAC verifies. Read the RAW bytes; model binding would mutate them
        // and break signature verification.
        string payload;
        using (var reader = new StreamReader(Request.Body))
        {
            payload = await reader.ReadToEndAsync(ct);
        }

        var signatureHeader = Request.Headers["Stripe-Signature"].ToString();
        if (string.IsNullOrEmpty(signatureHeader))
        {
            // greybeard: no secrets in logs -- log the fact, not the payload or header contents.
            _logger.LogWarning("Stripe webhook rejected: missing signature header.");
            return BadRequest();
        }

        Event stripeEvent;
        try
        {
            // greybeard: signature verification with bounded tolerance rejects replayed/forged
            // events outside the timestamp window. throwOnApiVersionMismatch guards against a
            // Stripe API upgrade silently changing the payload shape under us.
            stripeEvent = EventUtility.ConstructEvent(
                payload,
                signatureHeader,
                _options.SigningSecret,
                tolerance: 300, // 5 min, Stripe's recommended default
                throwOnApiVersionMismatch: true);
        }
        catch (StripeException ex)
        {
            // greybeard: signature failure is a 400, NOT a 500 -- a 5xx makes Stripe retry a
            // forged/garbage request forever. We never echo the exception detail back to the caller.
            _logger.LogWarning(ex, "Stripe webhook signature verification failed.");
            return BadRequest();
        }

        // We only act on the success event. Everything else is acknowledged so Stripe stops
        // redelivering it -- an un-acked event we don't care about is just noise on the queue.
        if (stripeEvent.Type != Events.PaymentIntentSucceeded)
        {
            _logger.LogInformation("Stripe webhook ignored: event {EventId} type {EventType}.",
                stripeEvent.Id, stripeEvent.Type);
            return Ok();
        }

        if (stripeEvent.Data.Object is not PaymentIntent intent)
        {
            _logger.LogWarning("Stripe webhook malformed: event {EventId} had no PaymentIntent.", stripeEvent.Id);
            return BadRequest();
        }

        // greybeard: money correctness -- Stripe sends `amount_received` as an integer in the
        // currency's minor unit (cents). Keep it a long all the way to the ledger. We NEVER
        // convert to decimal/double "dollars". Currency code travels with the amount.
        long amountMinorUnits = intent.AmountReceived;
        string currency = intent.Currency; // ISO-4217, lower-case from Stripe

        // greybeard: trust-boundary validation -- a malicious/buggy upstream could send a
        // non-positive amount or empty currency even past signature checks. Reject defensively.
        if (amountMinorUnits <= 0 || string.IsNullOrWhiteSpace(currency))
        {
            _logger.LogWarning("Stripe webhook rejected: event {EventId} non-positive amount or missing currency.", stripeEvent.Id);
            return BadRequest();
        }

        // greybeard: the account to credit must be one WE control, carried in metadata we set at
        // PaymentIntent creation -- never trust a customer-supplied id from the raw payload body.
        if (!intent.Metadata.TryGetValue("account_id", out var accountIdRaw)
            || !Guid.TryParse(accountIdRaw, out var accountId))
        {
            _logger.LogWarning("Stripe webhook rejected: event {EventId} missing/invalid account_id metadata.", stripeEvent.Id);
            return BadRequest();
        }

        try
        {
            await CreditAccountIdempotentlyAsync(stripeEvent.Id, accountId, amountMinorUnits, currency, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // greybeard: idempotency / concurrency -- a redelivery (or a racing duplicate) lost the
            // insert race on the unique event_id. The original already credited exactly once.
            // Return 200 so Stripe considers it handled. This is the happy path of "ran twice".
            _logger.LogInformation("Stripe webhook duplicate ignored (already processed): event {EventId}.", stripeEvent.Id);
            return Ok();
        }
        catch (Exception ex)
        {
            // greybeard: fail half-way -- the transaction rolled back, so NOTHING was credited.
            // Return 500 so Stripe redelivers; the retry will re-run the whole exactly-once path.
            // We log without the payload so no card/PII leaks into logs.
            _logger.LogError(ex, "Stripe webhook processing failed; will be retried: event {EventId}.", stripeEvent.Id);
            return StatusCode(500);
        }

        // greybeard: observability -- one structured success line keyed on the event id and account,
        // amount in minor units + currency. No secrets, no full payload.
        _logger.LogInformation(
            "Credited account {AccountId} with {Amount} {Currency} for event {EventId}.",
            accountId, amountMinorUnits, currency, stripeEvent.Id);

        return Ok();
    }

    /// <summary>
    /// Records the Stripe event and credits the account in a single atomic transaction.
    /// The unique index on ProcessedWebhookEvent.EventId is the exactly-once guarantee.
    /// </summary>
    private async Task CreditAccountIdempotentlyAsync(
        string eventId, Guid accountId, long amountMinorUnits, string currency, CancellationToken ct)
    {
        // greybeard: concurrency + fail-half-way -- Serializable isolation + a single transaction
        // means the dedup-insert, the credit, and the ledger entry commit together or not at all.
        // Two concurrent redeliveries cannot both win: one inserts the event row, the other hits
        // the unique violation and is rejected before any double-credit can occur.
        await using var tx = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);

        // Insert-first as the idempotency gate. If this row already exists, SaveChanges throws a
        // unique violation and the caller turns it into a clean 200 (no-op redelivery).
        _db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            EventId = eventId,
            ProcessedAtUtc = DateTime.UtcNow,
        });

        var account = await _db.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);

        if (account is null)
        {
            // greybeard: data-loss safety -- we will not silently drop a real payment. Roll back and
            // surface as a retryable failure; an operator/alert resolves the missing account rather
            // than the money vanishing. (Throwing rolls back the dedup insert too -> safe to retry.)
            throw new InvalidOperationException($"Account {accountId} not found for credit.");
        }

        // greybeard: money correctness -- guard against crediting across currencies. A mismatch is a
        // data integrity problem, not something to paper over with an FX guess.
        if (!string.Equals(account.Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Currency mismatch for account {accountId}: account {account.Currency} vs event {currency}.");
        }

        // greybeard: money correctness -- integer addition of minor units. No float, no rounding,
        // no lost half-cent.
        account.BalanceMinorUnits = checked(account.BalanceMinorUnits + amountMinorUnits);

        // greybeard: auditability -- an immutable ledger entry per credit, so balance is always
        // reconstructable from events (recovery path for any future balance dispute).
        _db.LedgerEntries.Add(new LedgerEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            AmountMinorUnits = amountMinorUnits,
            Currency = currency,
            SourceEventId = eventId,
            CreatedAtUtc = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// True when the exception is a unique-constraint violation (provider-specific SQLSTATE).
    /// Postgres: 23505. SQL Server: 2601/2627.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var sqlState = (ex.InnerException as dynamic)?.SqlState as string;
        if (sqlState == "23505") return true;

        var number = (ex.InnerException as dynamic)?.Number as int?;
        return number is 2601 or 2627;
    }
}

// ---- Supporting types (would live in their own files) -------------------------------------

public sealed class StripeWebhookOptions
{
    // greybeard: no secrets in source -- bound from configuration/secret store, never hard-coded.
    public string SigningSecret { get; set; } = string.Empty;
}

public sealed class ProcessedWebhookEvent
{
    // greybeard: idempotency key -- EventId carries a UNIQUE index in the model configuration.
    // That index is the single source of truth for exactly-once processing.
    public string EventId { get; set; } = string.Empty;
    public DateTime ProcessedAtUtc { get; set; }
}

public sealed class Account
{
    public Guid Id { get; set; }
    // greybeard: money stored as integer minor units alongside its ISO-4217 currency.
    public long BalanceMinorUnits { get; set; }
    public string Currency { get; set; } = string.Empty;
}

public sealed class LedgerEntry
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public long AmountMinorUnits { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string SourceEventId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public class LedgerDbContext : DbContext
{
    public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options) { }

    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var ev = b.Entity<ProcessedWebhookEvent>();
        ev.HasKey(e => e.EventId);
        // greybeard: the unique index that makes redelivery a no-op instead of a double-credit.
        ev.HasIndex(e => e.EventId).IsUnique();

        b.Entity<Account>().HasKey(a => a.Id);
        b.Entity<LedgerEntry>().HasKey(l => l.Id);
    }
}

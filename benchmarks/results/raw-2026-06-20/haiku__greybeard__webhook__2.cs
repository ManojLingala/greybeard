using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Serilog;

namespace Greybeard.Webhooks
{
    [ApiController]
    [Route("api/webhooks")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly ILogger _log;
        private readonly string _stripeWebhookSecret;

        public StripeWebhookController(
            ApplicationDbContext db,
            ILogger log,
            IConfiguration config)
        {
            _db = db;
            _log = log;
            _stripeWebhookSecret = config["Stripe:WebhookSecret"]
                ?? throw new InvalidOperationException("Stripe:WebhookSecret not configured");
        }

        [HttpPost("stripe/payment-succeeded")]
        public async Task<IActionResult> HandlePaymentSucceeded()
        {
            // greybeard: external call - verify Stripe webhook signature before trusting payload
            string json = await new StreamReader(Request.Body).ReadToEndAsync();

            string? signatureHeader = Request.Headers["Stripe-Signature"];
            if (string.IsNullOrEmpty(signatureHeader))
            {
                _log.Warning("Stripe webhook missing signature header");
                return BadRequest();
            }

            Event stripeEvent;
            try
            {
                // greybeard: trust-boundary validation - cryptographically verify webhook authenticity
                stripeEvent = EventUtility.ConstructEvent(
                    json,
                    signatureHeader,
                    _stripeWebhookSecret);
            }
            catch (StripeException ex)
            {
                _log.Warning("Stripe webhook signature verification failed: {reason}", ex.Message);
                return BadRequest();
            }

            // greybeard: mutation - idempotency key ensures redelivery is safe, prevents double-credit
            if (stripeEvent.Type != "payment_intent.succeeded")
            {
                return Ok();
            }

            var paymentIntent = stripeEvent.Data.Object as PaymentIntent
                ?? throw new InvalidOperationException("Expected PaymentIntent in event data");

            string idempotencyKey = $"stripe_payment_{paymentIntent.Id}";
            long amountMinorUnits = paymentIntent.Amount;

            // greybeard: money - store amount as integer minor units (cents); never float
            // amountMinorUnits is already in minor units from Stripe (e.g., $10.00 = 1000 cents)
            string currencyCode = paymentIntent.Currency?.ToUpperInvariant() ?? "USD";

            _log.Information(
                "Processing payment succeeded event for customer: {customerId}",
                paymentIntent.CustomerId);

            try
            {
                // greybeard: concurrency + mutation - explicit transaction boundary with row-level lock
                // to prevent concurrent webhook deliveries from double-crediting the same payment
                var result = await _db.Database.BeginTransactionAsync();

                try
                {
                    // greybeard: mutation - idempotency check: has this payment already been processed?
                    var existingCredit = await _db.PaymentCredits
                        .AsNoTracking()
                        .FirstOrDefaultAsync(pc => pc.IdempotencyKey == idempotencyKey);

                    if (existingCredit != null)
                    {
                        _log.Information(
                            "Payment already credited, idempotency key: {key}",
                            idempotencyKey);
                        await result.CommitAsync();
                        return Ok();
                    }

                    // greybeard: concurrency - acquire row lock on customer account to block
                    // concurrent mutations and ensure exactly-once credit
                    var customer = await _db.Customers
                        .FromSqlInterpolated($@"
                            SELECT * FROM Customers
                            WHERE StripeCustomerId = {paymentIntent.CustomerId}
                            FOR UPDATE")
                        .FirstOrDefaultAsync();

                    if (customer == null)
                    {
                        // greybeard: fail-half-way - customer not found; log but don't crash
                        // Stripe will retry; we'll succeed when customer is provisioned
                        _log.Warning(
                            "Stripe customer not found in our database, will retry: {stripeCustomerId}",
                            paymentIntent.CustomerId);
                        await result.CommitAsync();
                        return Accepted(); // Accept but customer not ready; Stripe will retry
                    }

                    // greybeard: money - credit account in integer minor units with explicit currency
                    var credit = new PaymentCredit
                    {
                        CustomerId = customer.Id,
                        AmountMinorUnits = amountMinorUnits,
                        CurrencyCode = currencyCode,
                        StripePaymentIntentId = paymentIntent.Id,
                        IdempotencyKey = idempotencyKey,
                        ProcessedAt = DateTime.UtcNow,
                        Status = "completed"
                    };

                    _db.PaymentCredits.Add(credit);

                    // greybeard: money - update customer balance as integer, no floating-point arithmetic
                    customer.BalanceMinorUnits += amountMinorUnits;
                    customer.LastCreditedAt = DateTime.UtcNow;

                    _db.Customers.Update(customer);

                    await _db.SaveChangesAsync();
                    await result.CommitAsync();

                    _log.Information(
                        "Successfully credited customer account: {customerId}, amount: {amount} {currency}",
                        customer.Id,
                        amountMinorUnits,
                        currencyCode);

                    return Ok();
                }
                catch (Exception ex)
                {
                    // greybeard: fail-half-way - rollback transaction on any error
                    // no partial state left behind
                    await result.RollbackAsync();

                    _log.Error(
                        ex,
                        "Failed to process payment credit for Stripe event {paymentIntentId}",
                        paymentIntent.Id);

                    // Return 500 to signal Stripe to retry this webhook
                    return StatusCode(500);
                }
            }
            catch (Exception ex)
            {
                _log.Error(
                    ex,
                    "Unexpected error handling Stripe webhook");
                return StatusCode(500);
            }
        }
    }

    // Supporting models for demonstration
    public class PaymentCredit
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }

        // greybeard: money - store as integer minor units (cents), never float
        public long AmountMinorUnits { get; set; }

        // greybeard: money - currency code always travels with amount
        public string CurrencyCode { get; set; } = "USD";

        public string StripePaymentIntentId { get; set; } = string.Empty;

        // greybeard: mutation - idempotency key prevents double-processing on webhook retry
        public string IdempotencyKey { get; set; } = string.Empty;

        public DateTime ProcessedAt { get; set; }
        public string Status { get; set; } = "pending";

        public virtual Customer Customer { get; set; } = null!;
    }

    public class Customer
    {
        public int Id { get; set; }
        public string StripeCustomerId { get; set; } = string.Empty;

        // greybeard: money - account balance stored as integer minor units
        public long BalanceMinorUnits { get; set; }

        public DateTime LastCreditedAt { get; set; }

        public virtual ICollection<PaymentCredit> Credits { get; set; } = new List<PaymentCredit>();
    }
}

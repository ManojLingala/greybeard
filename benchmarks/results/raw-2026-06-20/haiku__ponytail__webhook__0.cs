using Microsoft.AspNetCore.Mvc;
using Stripe;
using System;
using System.IO;
using System.Threading.Tasks;

[ApiController]
[Route("api/webhooks")]
public class StripeWebhookController : ControllerBase
{
    private readonly string _webhookSecret;
    private readonly ICustomerService _customerService;

    public StripeWebhookController(IConfiguration config, ICustomerService customerService)
    {
        _webhookSecret = config["Stripe:WebhookSecret"];
        _customerService = customerService;
    }

    [HttpPost("payment-succeeded")]
    public async Task<IActionResult> HandlePaymentSucceeded()
    {
        // Read raw request body for signature verification
        var json = await new StreamReader(Request.Body).ReadToEndAsync();

        try
        {
            // Verify webhook signature
            var stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _webhookSecret
            );

            // Only process payment_intent.succeeded events
            if (stripeEvent.Type != Events.PaymentIntentSucceeded)
                return Ok();

            var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
            if (paymentIntent == null)
                return BadRequest("Invalid payment intent");

            // Idempotent credit: check if already processed using idempotency key
            var idempotencyKey = stripeEvent.Id; // Stripe event ID ensures uniqueness
            var alreadyProcessed = await _customerService.IsWebhookProcessedAsync(idempotencyKey);

            if (alreadyProcessed)
                return Ok(); // Redelivery - already credited, return 200 to acknowledge

            // Extract customer ID and amount
            var customerId = paymentIntent.Metadata?.ContainsKey("customer_id") == true
                ? paymentIntent.Metadata["customer_id"]
                : paymentIntent.CustomerId;

            var amountInCents = paymentIntent.Amount.Value;

            // Credit the customer's account
            await _customerService.CreditAccountAsync(customerId, amountInCents, idempotencyKey);

            return Ok();
        }
        catch (StripeException ex)
        {
            // Signature verification failed or invalid event
            return Unauthorized(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            // Log error but return 500 so Stripe retries
            // (Don't return 400/401 as Stripe won't retry those)
            Console.WriteLine($"Webhook processing error: {ex.Message}");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}

// Example ICustomerService implementation
public interface ICustomerService
{
    Task<bool> IsWebhookProcessedAsync(string webhookEventId);
    Task CreditAccountAsync(string customerId, long amountInCents, string idempotencyKey);
}

public class CustomerService : ICustomerService
{
    private readonly IDbContext _db;

    public CustomerService(IDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsWebhookProcessedAsync(string webhookEventId)
    {
        return await _db.ProcessedWebhooks
            .AnyAsync(w => w.EventId == webhookEventId);
    }

    public async Task CreditAccountAsync(string customerId, long amountInCents, string idempotencyKey)
    {
        using (var transaction = _db.Database.BeginTransaction())
        {
            try
            {
                // Check again inside transaction (prevent race condition)
                var exists = await _db.ProcessedWebhooks
                    .AnyAsync(w => w.EventId == idempotencyKey);

                if (exists)
                    return;

                // Credit customer balance
                var customer = await _db.Customers.FindAsync(customerId);
                if (customer != null)
                {
                    customer.Balance += amountInCents;
                    _db.Customers.Update(customer);
                }

                // Record webhook as processed
                _db.ProcessedWebhooks.Add(new ProcessedWebhook
                {
                    EventId = idempotencyKey,
                    ProcessedAt = DateTime.UtcNow
                });

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }
}

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Stripe;
using Stripe.BillingPortal;

namespace WebhookHandlers.Controllers
{
    [ApiController]
    [Route("api/webhooks")]
    public class StripeWebhookController : ControllerBase
    {
        private readonly string _stripeWebhookSecret;
        private readonly IAccountService _accountService;

        public StripeWebhookController(IConfiguration config, IAccountService accountService)
        {
            _stripeWebhookSecret = config["Stripe:WebhookSecret"];
            _accountService = accountService;
        }

        [HttpPost("stripe")]
        public async Task<IActionResult> HandleStripeWebhook()
        {
            // Read the raw body for signature verification
            string json;
            using (var reader = new StreamReader(HttpContext.Request.Body))
            {
                json = await reader.ReadToEndAsync();
            }

            try
            {
                // Verify the webhook signature
                var stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    _stripeWebhookSecret
                );

                // Handle only payment_intent.succeeded events
                if (stripeEvent.Type == "payment_intent.succeeded")
                {
                    var paymentIntent = stripeEvent.Data.Object as PaymentIntent;

                    if (paymentIntent == null)
                    {
                        return BadRequest("Invalid payment intent object");
                    }

                    // Idempotency: check if we've already processed this event
                    var alreadyProcessed = await _accountService.IsEventProcessedAsync(stripeEvent.Id);
                    if (alreadyProcessed)
                    {
                        // Return 200 OK for idempotency - already handled this event
                        return Ok();
                    }

                    // Extract customer and amount information
                    var customerId = paymentIntent.CustomerId;
                    var amountCents = paymentIntent.Amount ?? 0;
                    var amountDollars = amountCents / 100m;

                    // Credit the customer's account
                    var creditResult = await _accountService.CreditCustomerAccountAsync(
                        customerId,
                        amountDollars,
                        stripeEvent.Id
                    );

                    if (!creditResult.Success)
                    {
                        // Log the failure but still return 200 to prevent redelivery storms
                        // The webhook will be retried if we return an error
                        Console.WriteLine($"Failed to credit account for customer {customerId}: {creditResult.Message}");
                    }

                    // Mark this event as processed for idempotency
                    await _accountService.MarkEventAsProcessedAsync(stripeEvent.Id);
                }

                // Return 200 OK to acknowledge receipt
                return Ok();
            }
            catch (StripeAPIException ex)
            {
                // Signature verification failed or other Stripe API error
                Console.WriteLine($"Stripe API Exception: {ex.Message}");
                return Unauthorized("Invalid signature");
            }
            catch (Exception ex)
            {
                // Log unexpected errors
                Console.WriteLine($"Webhook processing error: {ex.Message}");

                // Return 500 to trigger Stripe's automatic retry logic
                return StatusCode(500, "Webhook processing failed");
            }
        }
    }

    // Helper service interface for account operations
    public interface IAccountService
    {
        Task<bool> IsEventProcessedAsync(string eventId);
        Task MarkEventAsProcessedAsync(string eventId);
        Task<CreditResult> CreditCustomerAccountAsync(string customerId, decimal amount, string eventId);
    }

    public class CreditResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}

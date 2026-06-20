using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Stripe;
using Stripe.Events;

[ApiController]
[Route("api/[controller]")]
public class StripeWebhookController : ControllerBase
{
    private readonly string _stripeWebhookSecret;
    private readonly IPaymentService _paymentService;

    public StripeWebhookController(IConfiguration configuration, IPaymentService paymentService)
    {
        _stripeWebhookSecret = configuration["Stripe:WebhookSecret"];
        _paymentService = paymentService;
    }

    [HttpPost("payment-webhook")]
    public async Task<IActionResult> HandlePaymentWebhook()
    {
        // Read the request body as a string
        string json;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            json = await reader.ReadToEndAsync();
        }

        try
        {
            // Verify the webhook signature using Stripe's security header
            var stripeSignatureHeader = Request.Headers["Stripe-Signature"].ToString();

            if (string.IsNullOrEmpty(stripeSignatureHeader) || string.IsNullOrEmpty(_stripeWebhookSecret))
            {
                return Unauthorized("Invalid webhook signature header or missing secret");
            }

            // Use Stripe's utility to construct and verify the event
            var stripeEvent = EventUtility.ConstructEvent(json, stripeSignatureHeader, _stripeWebhookSecret);

            // Handle the payment_intent.succeeded event
            if (stripeEvent.Type == Events.PaymentIntentSucceeded)
            {
                var paymentIntent = stripeEvent.Data.Object as PaymentIntent;

                if (paymentIntent == null)
                {
                    return BadRequest("Invalid payment intent data");
                }

                // Extract customer information
                var customerId = paymentIntent.CustomerId;
                var amount = paymentIntent.Amount;
                var currency = paymentIntent.Currency;
                var paymentIntentId = paymentIntent.Id;

                // Check for idempotency - ensure we don't double-credit on redelivery
                var isProcessed = await _paymentService.IsWebhookEventProcessedAsync(stripeEvent.Id);

                if (isProcessed)
                {
                    // Already handled this webhook event, return success to prevent retries
                    return Ok(new { message = "Webhook already processed" });
                }

                // Credit the customer's account
                var creditSuccess = await _paymentService.CreditCustomerAccountAsync(
                    customerId,
                    amount,
                    currency,
                    paymentIntentId
                );

                if (!creditSuccess)
                {
                    return StatusCode(500, "Failed to credit customer account");
                }

                // Mark this webhook event as processed to handle redelivery correctly
                await _paymentService.MarkWebhookEventProcessedAsync(stripeEvent.Id);

                return Ok(new { message = "Payment processed successfully" });
            }

            // For other event types, acknowledge receipt
            return Ok(new { message = "Webhook received" });
        }
        catch (StripeException stripeEx)
        {
            // Invalid signature - reject the webhook
            return Unauthorized($"Webhook verification failed: {stripeEx.Message}");
        }
        catch (Exception ex)
        {
            // Log the exception and return error
            // In production, log this securely
            return StatusCode(500, $"Internal server error processing webhook: {ex.Message}");
        }
    }
}

/// <summary>
/// Interface for payment operations
/// </summary>
public interface IPaymentService
{
    Task<bool> CreditCustomerAccountAsync(string customerId, long amount, string currency, string paymentIntentId);
    Task<bool> IsWebhookEventProcessedAsync(string eventId);
    Task MarkWebhookEventProcessedAsync(string eventId);
}

/// <summary>
/// Example implementation of IPaymentService
/// </summary>
public class PaymentService : IPaymentService
{
    private readonly IDatabase _database;

    public PaymentService(IDatabase database)
    {
        _database = database;
    }

    public async Task<bool> CreditCustomerAccountAsync(string customerId, long amount, string currency, string paymentIntentId)
    {
        try
        {
            // Convert amount from cents to appropriate units
            decimal creditAmount = amount / 100m;

            // Begin transaction for atomicity
            using (var transaction = await _database.BeginTransactionAsync())
            {
                // Credit the customer's account in your database
                var customer = await _database.GetCustomerAsync(customerId);
                if (customer == null)
                {
                    await transaction.RollbackAsync();
                    return false;
                }

                customer.AccountBalance += creditAmount;
                await _database.UpdateCustomerAsync(customer);

                // Log the transaction
                await _database.LogPaymentTransactionAsync(new PaymentLog
                {
                    CustomerId = customerId,
                    Amount = creditAmount,
                    Currency = currency,
                    StripePaymentIntentId = paymentIntentId,
                    ProcessedAt = DateTime.UtcNow
                });

                await transaction.CommitAsync();
                return true;
            }
        }
        catch (Exception ex)
        {
            // Log error
            return false;
        }
    }

    public async Task<bool> IsWebhookEventProcessedAsync(string eventId)
    {
        var record = await _database.GetWebhookEventRecordAsync(eventId);
        return record != null;
    }

    public async Task MarkWebhookEventProcessedAsync(string eventId)
    {
        await _database.LogWebhookEventAsync(new WebhookEventRecord
        {
            StripeEventId = eventId,
            ProcessedAt = DateTime.UtcNow
        });
    }
}

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Stripe;

[ApiController]
[Route("api/webhooks")]
public class StripeWebhookController : ControllerBase
{
    private readonly string _webhookSecret;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly IPaymentService _paymentService;

    public StripeWebhookController(
        IConfiguration config,
        ILogger<StripeWebhookController> logger,
        IPaymentService paymentService)
    {
        _webhookSecret = config["Stripe:WebhookSecret"];
        _logger = logger;
        _paymentService = paymentService;
    }

    [HttpPost("payment")]
    public async Task<IActionResult> HandlePaymentWebhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

        try
        {
            var stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _webhookSecret);

            // Handle idempotency: check if this event was already processed
            if (await _paymentService.EventProcessedAsync(stripeEvent.Id))
            {
                _logger.LogInformation($"Webhook event {stripeEvent.Id} already processed, returning success");
                return Ok();
            }

            if (stripeEvent.Type == Events.ChargeSucceeded)
            {
                var charge = stripeEvent.Data.Object as Charge;
                await _paymentService.CreditAccountAsync(charge.CustomerId, charge.Amount);
                _logger.LogInformation($"Credited account for customer {charge.CustomerId}");
            }

            // Mark event as processed
            await _paymentService.MarkEventProcessedAsync(stripeEvent.Id);
            return Ok();
        }
        catch (StripeException ex)
        {
            _logger.LogError($"Stripe webhook verification failed: {ex.Message}");
            return BadRequest();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Webhook processing error: {ex.Message}");
            return StatusCode(500);
        }
    }
}

public interface IPaymentService
{
    Task<bool> EventProcessedAsync(string eventId);
    Task MarkEventProcessedAsync(string eventId);
    Task CreditAccountAsync(string customerId, long amountCents);
}

using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Payments.Controllers;

// YAGNI: lean on Stripe.NET for signature verification and on a single DB
// uniqueness constraint for idempotency. No custom crypto, no bespoke queue.
[ApiController]
[Route("webhooks/stripe")]
public class StripeWebhookController : ControllerBase
{
    private readonly IAccountCreditService _credits;
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly string _webhookSecret;

    public StripeWebhookController(
        IAccountCreditService credits,
        ILogger<StripeWebhookController> logger,
        IConfiguration config)
    {
        _credits = credits;
        _logger = logger;
        // The signing secret ("whsec_...") from the Stripe dashboard / CLI.
        _webhookSecret = config["Stripe:WebhookSecret"]
            ?? throw new InvalidOperationException("Stripe:WebhookSecret is not configured.");
    }

    [HttpPost]
    public async Task<IActionResult> Handle()
    {
        // Read the raw body. Signature verification requires the exact bytes
        // Stripe sent, so we must NOT use a model-bound DTO here.
        string payload = await new StreamReader(Request.Body).ReadToEndAsync();

        Event stripeEvent;
        try
        {
            // ConstructEvent verifies the Stripe-Signature header against the
            // signing secret AND enforces the timestamp tolerance (replay guard).
            // Throws StripeException on any mismatch.
            stripeEvent = EventUtility.ConstructEvent(
                payload,
                Request.Headers["Stripe-Signature"],
                _webhookSecret);
        }
        catch (StripeException ex)
        {
            // Bad/forged signature or stale timestamp -> reject. 400 tells Stripe
            // not to keep retrying a request it can never make valid.
            _logger.LogWarning(ex, "Rejected Stripe webhook: signature verification failed.");
            return BadRequest();
        }

        // We only care about successful payments here. Acknowledge everything
        // else with 200 so Stripe stops resending events we intentionally ignore.
        if (stripeEvent.Type != EventTypes.PaymentIntentSucceeded)
        {
            return Ok();
        }

        if (stripeEvent.Data.Object is not PaymentIntent intent)
        {
            _logger.LogWarning("payment_intent.succeeded event {EventId} had an unexpected payload.", stripeEvent.Id);
            return Ok();
        }

        try
        {
            // Idempotency for redeliveries: Stripe may deliver the same event
            // more than once (retries, network hiccups). We pass the event id and
            // let the service apply the credit exactly once. The implementation
            // records the event id under a unique constraint inside the same
            // transaction as the credit, so a duplicate is a no-op.
            await _credits.CreditOnceAsync(
                eventId: stripeEvent.Id,
                customerId: intent.CustomerId,
                amount: intent.AmountReceived,   // smallest currency unit (e.g. cents)
                currency: intent.Currency);
        }
        catch (Exception ex)
        {
            // Transient failure (DB down, etc.): return 500 so Stripe retries
            // later with the same event id, which our idempotency key absorbs.
            _logger.LogError(ex, "Failed to credit account for Stripe event {EventId}.", stripeEvent.Id);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        // Acknowledge quickly so Stripe marks the event as delivered.
        return Ok();
    }
}

/// <summary>
/// Credits an account exactly once per Stripe event id.
/// Implementations should persist the event id under a unique constraint in the
/// SAME transaction that applies the credit, treating a duplicate-key violation
/// as a successful no-op (the credit was already applied for this event).
/// </summary>
public interface IAccountCreditService
{
    Task CreditOnceAsync(string eventId, string? customerId, long amount, string currency);
}

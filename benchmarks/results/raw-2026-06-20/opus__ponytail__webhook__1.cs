using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Payments.Controllers;

// Minimal, correct Stripe webhook handler.
// - Verifies the signature using Stripe's official helper (handles timestamp +
//   HMAC tolerance, which guards against replay/forgery). No hand-rolled crypto.
// - Treats delivery as at-least-once: Stripe retries, so the credit operation
//   must be idempotent. We delegate that to the account service keyed by the
//   Stripe event id.
// - Always 200 on events we accept (even if we ignore the type) so Stripe stops
//   retrying; only 400 for genuinely bad/unverifiable payloads.
[ApiController]
[Route("webhooks/stripe")]
public sealed class StripeWebhookController : ControllerBase
{
    private readonly string _webhookSecret;
    private readonly IAccountCreditService _credits;
    private readonly ILogger<StripeWebhookController> _logger;

    public StripeWebhookController(
        IAccountCreditService credits,
        ILogger<StripeWebhookController> logger,
        // Inject the signing secret (whsec_...) from configuration, not a literal.
        IStripeWebhookSecret secret)
    {
        _credits = credits;
        _logger = logger;
        _webhookSecret = secret.Value;
    }

    [HttpPost]
    public async Task<IActionResult> Handle(CancellationToken ct)
    {
        // Read the raw body exactly as sent — signature verification is byte-sensitive.
        string payload;
        using (var reader = new StreamReader(Request.Body))
        {
            payload = await reader.ReadToEndAsync(ct);
        }

        Event stripeEvent;
        try
        {
            // Throws StripeException if the signature is missing/invalid or the
            // timestamp is outside the default tolerance (replay protection).
            stripeEvent = EventUtility.ConstructEvent(
                payload,
                Request.Headers["Stripe-Signature"],
                _webhookSecret);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Rejected Stripe webhook: signature verification failed.");
            return BadRequest();
        }

        // We only care about successful payments here. Acknowledge everything
        // else with 200 so Stripe doesn't keep retrying types we don't handle.
        if (stripeEvent.Type != EventTypes.PaymentIntentSucceeded)
        {
            return Ok();
        }

        var intent = (PaymentIntent)stripeEvent.Data.Object;

        // Idempotency: Stripe delivers at least once. Key the credit on the event
        // id so a redelivery is a no-op. The service is responsible for doing this
        // atomically (e.g. unique constraint on the event id).
        await _credits.CreditAsync(
            eventId: stripeEvent.Id,
            customerId: intent.CustomerId,
            amount: intent.AmountReceived,
            currency: intent.Currency,
            ct);

        return Ok();
    }
}

// Provided elsewhere in the app. Implementations MUST be idempotent on eventId.
public interface IAccountCreditService
{
    Task CreditAsync(
        string eventId,
        string customerId,
        long amount,
        string currency,
        CancellationToken ct);
}

// Thin wrapper so the signing secret is injected rather than read inline.
public interface IStripeWebhookSecret
{
    string Value { get; }
}

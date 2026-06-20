# 2. Stripe webhook — the flagship survivor

This is the canonical 3am page: a webhook handler that looks correct, demos fine,
and quietly leaks money in production. Two parts — the **secure handler** and the
**reconciliation sweep** that catches what the handler never received.

**Task:** "Handle the Stripe `payment_succeeded` webhook and credit the account."

---

## Part A — the secure handler

### Without greybeard

```csharp
[HttpPost("/webhooks/stripe")]
public async Task<IActionResult> Handle([FromBody] StripeEvent e)   // body already parsed
{
    // no signature check — anyone who finds this URL can POST a fake event
    await _accounts.Credit(e.Data.CustomerId, e.Data.Amount);       // trusts the payload amount
    return Ok();                                                    // runs every redelivery
}
```

Four production bugs in five lines:
- **No signature verification** → forge a `payment_succeeded`, credit yourself.
- **Trusts the payload amount** → attacker (or a bug) sets any number.
- **No replay protection** → a captured request works forever.
- **Not idempotent** → at-least-once delivery double-credits real customers.

### With greybeard

```csharp
// greybeard[W1/W2:verify]: HMAC the RAW bytes against the endpoint secret. Read the body as a string,
// NOT [FromBody] — parsing first re-serializes and the signature will never match (then people disable it).
[HttpPost("/webhooks/stripe")]
public async Task<IActionResult> Handle(CancellationToken ct)
{
    using var reader = new StreamReader(Request.Body);
    var rawBody = await reader.ReadToEndAsync(ct);
    var sigHeader = Request.Headers["Stripe-Signature"];

    Event stripeEvent;
    try
    {
        // greybeard[W3:replay]: ConstructEvent enforces the signed-timestamp tolerance (default 5 min); old replays rejected.
        stripeEvent = EventUtility.ConstructEvent(rawBody, sigHeader, _endpointSecret);
    }
    catch (StripeException)
    {
        return BadRequest();   // greybeard[trust-boundary]: unverified input never reaches business logic
    }

    if (stripeEvent.Type != "payment_intent.succeeded") return Ok();
    var intent = (PaymentIntent)stripeEvent.Data.Object;

    await using var tx = await _db.Database.BeginTransactionAsync(ct);

    // greybeard[W5:idempotency]: dedupe on the PAYMENT, not the raw event id, in the same tx as the credit —
    // so the reconciliation sweep (Part B) collides on the SAME key and can never double-credit. Replay = no-op.
    if (!await TryRecordEvent($"credit:{intent.Id}", ct))
        return Ok();

    // greybeard[W4:trust]: do NOT credit the amount from the payload. Re-fetch from Stripe (or match our own
    // order record) so a tampered/forged amount can't move money. greybeard[1:money]: minor units, integer.
    var confirmed = await _stripe.PaymentIntents.GetAsync(intent.Id, cancellationToken: ct);
    if (confirmed.Status != "succeeded") return Ok();

    await _accounts.CreditMinor(confirmed.Metadata["customer_id"], confirmed.AmountReceived);
    await tx.CommitAsync(ct);

    // greybeard[W7:async]: heavy work (emails, fulfilment) goes on a queue, not in the webhook request.
    await _queue.Enqueue(new FulfilOrder(confirmed.Id), ct);
    return Ok();
}
```

---

## Part B — the reconciliation sweep (the part everyone skips)

Even a perfect handler misses events: your endpoint 500s during a deploy, the
retry window lapses, Stripe has an incident. **Webhook delivery is best-effort —
reconciliation is the source of truth.** Run this on a schedule.

```csharp
// greybeard[W6:reconcile]: pull the provider's record of truth and repair the diff. Idempotent by design —
// it reuses the same TryRecordEvent + CreditMinor path, so re-running is always safe.
public async Task ReconcileSince(DateTime sinceUtc, CancellationToken ct)
{
    var options = new PaymentIntentListOptions
    {
        Created = new DateRangeOptions { GreaterThanOrEqual = sinceUtc },
        Limit = 100,
    };

    await foreach (var pi in _stripe.PaymentIntents.ListAutoPagingAsync(options, cancellationToken: ct))
    {
        if (pi.Status != "succeeded") continue;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // greybeard[W5:idempotency]: the SAME payment-scoped key the handler uses ($"credit:{id}"), so a payment
        // already credited by the handler collides here and is skipped. Missed payments get credited exactly once.
        if (await TryRecordEvent($"credit:{pi.Id}", ct))
        {
            await _accounts.CreditMinor(pi.Metadata["customer_id"], pi.AmountReceived);
            _logger.LogWarning("Reconciliation credited missed payment {Pi}", pi.Id);  // a missed webhook is a signal, not silence
        }
        await tx.CommitAsync(ct);
    }
}
```

Both paths dedupe on the **same payment-scoped key** (`credit:{paymentId}`), so the
handler and the sweep can never double-credit the same payment — the sweep only
actually credits payments the handler never recorded. If you also need to audit
*which* `evt_...` drove each credit, store that event id in a separate column; never
make it the dedup key, or the two code paths stop colliding and the money leaks.

**Rungs:** W1–W7, plus 1 (money), 2 (idempotency), 4 (concurrency), trust-boundary.
**3am pages saved:** the forged-webhook fraud, the double-credit support flood, and
the silent revenue gap when 200 payments never fired a webhook.

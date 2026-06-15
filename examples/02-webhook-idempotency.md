# 2. Payment webhook — the replay that double-credits

**Task:** "Handle the Stripe `payment_succeeded` webhook and credit the account."

### Without greybeard

```csharp
[HttpPost("/webhooks/stripe")]
public async Task<IActionResult> Handle(StripeEvent e)
{
    var amount = e.Data.Amount;
    await _accounts.Credit(e.Data.CustomerId, amount);   // runs every time the event arrives
    return Ok();
}
```

Stripe (and every serious provider) delivers **at least once**. A network blip,
a retry, a redelivery — the customer gets credited twice. This is the single
most common money bug in webhook handlers.

### With greybeard

```csharp
[HttpPost("/webhooks/stripe")]
public async Task<IActionResult> Handle(StripeEvent e)
{
    // greybeard[2:idempotency]: dedupe on the provider event id. Exactly-once *effect*
    // on top of at-least-once *delivery*. Insert-if-absent inside the same tx as the credit.
    await using var tx = await _db.Database.BeginTransactionAsync();

    var firstTime = await _db.ProcessedEvents
        .Where(p => p.EventId == e.Id)
        .ExecuteUpdateAsync(_ => { }) == 0
        && await TryInsertEvent(e.Id);

    if (!firstTime)
        return Ok();  // greybeard[6:partial]: replay is a no-op, not an error

    // greybeard[1:money]: amount is minor units from the provider, used as-is, never re-parsed from a float
    await _accounts.CreditMinor(e.Data.CustomerId, e.Data.AmountMinor);
    await tx.CommitAsync();
    return Ok();
}
```

**Rungs:** 2 (idempotency), 1 (money), 6 (partial failure).
**3am page saved:** the support flood when 400 customers were credited twice.

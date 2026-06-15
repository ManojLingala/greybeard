# 6. Logging a charge — the secret that lands in the log

**Task:** "Log the charge attempt so we can debug failures."

### Without greybeard

```csharp
public async Task Charge(ChargeRequest req)
{
    _logger.LogInformation("Charging: {@Request}", req);  // serializes the WHOLE object
    // ... req contains CardNumber, Cvv, ApiToken ...
}
```

`{@Request}` serializes everything, so the full card number, CVV, and API token
land in your logs — which flow to a third-party log aggregator, get indexed, and
are now a compliance incident (PCI) waiting to be discovered in an audit.

### With greybeard

```csharp
// greybeard[non-negotiable:secrets]: never log secrets or PII. Log a redacted, structured view only.
// greybeard[observability]: correlation id travels with the log so the charge is traceable end-to-end.
public async Task Charge(ChargeRequest req, string correlationId)
{
    _logger.LogInformation(
        "Charging {Amount} {Currency} for {CustomerId} card ****{Last4} corr={Corr}",
        req.AmountMinor, req.Currency, req.CustomerId, req.CardLast4, correlationId);
    // CardNumber / Cvv / ApiToken are never passed to the logger.
    // Belt-and-braces: a Serilog destructuring policy strips these fields globally too.
}
```

**Rungs:** non-negotiable (secrets), observability.
**3am page saved:** the PCI audit finding that card numbers were sitting in plaintext logs.

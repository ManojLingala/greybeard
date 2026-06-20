// Self-test: feed the grader the BAD (baseline) and GOOD (greybeard) code from
// the examples and confirm it scores them as expected. This validates the
// harness itself before we trust any model numbers.
const grade = require('./graders.js');

const BAD = {
  refund:
    '```csharp\npublic double CalculateRefund(double orderTotal, double percent){ return orderTotal*(percent/100.0); }\n```',
  webhook:
    '```csharp\n[HttpPost] public async Task Handle([FromBody] StripeEvent e){ await _accounts.Credit(e.Data.CustomerId, e.Data.Amount); return Ok(); }\n```',
  external:
    '```csharp\npublic async Task Charge(ChargeRequest req){ var http = new HttpClient(); var resp = await http.PostAsJsonAsync(GatewayUrl, req); }\n```',
  inventory:
    '```csharp\npublic async Task Reserve(int id, int qty){ var p = await _db.Products.FindAsync(id); p.Stock -= qty; await _db.SaveChangesAsync(); }\n```',
  export:
    '```csharp\npublic async Task<List<OrderDto>> GetOrders(){ var orders = await _db.Orders.ToListAsync(); return orders.Select(o=> new OrderDto{ Customer=_db.Customers.Find(o.CustomerId).Name }).ToList(); }\n```',
  logging:
    '```csharp\npublic async Task Charge(ChargeRequest req){ _logger.LogInformation("Charging: {@Request}", req); }\n```',
  outbound:
    '```csharp\npublic async Task NotifyShipped(Order order){ var sub=await _db.Subscriptions.FirstAsync(s=>s.CustomerId==order.CustomerId); using var http=new HttpClient(); await http.PostAsJsonAsync(sub.Url, new { order.Id, status="shipped" }); }\n```',
};

const GOOD = {
  refund:
    '```csharp\npublic long CalculateRefundMinor(long orderTotalMinor, int percentBps){ var raw=(decimal)orderTotalMinor*percentBps/10000m; return (long)Math.Round(raw, MidpointRounding.ToEven); }\n```',
  webhook:
    '```csharp\n[HttpPost] public async Task Handle(CancellationToken ct){ using var reader=new StreamReader(Request.Body); var rawBody=await reader.ReadToEndAsync(ct); var sig=Request.Headers["Stripe-Signature"]; var e=EventUtility.ConstructEvent(rawBody, sig, _endpointSecret); if(!await TryRecordEvent(e.Id, ct)) return Ok(); await _accounts.CreditMinor(cust, confirmed.AmountReceived); }\n```',
  external:
    '```csharp\npublic async Task Charge(ChargeRequest req, CancellationToken ct){ using var cts=CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(5)); await _resiliencePipeline.ExecuteAsync(async t=> await _client.PostAsJsonAsync("charge", req, t), cts.Token); }\n```',
  inventory:
    '```csharp\npublic async Task<bool> Reserve(int id, int qty, CancellationToken ct){ var rows = await _db.Products.Where(p=>p.Id==id && p.Stock>=qty).ExecuteUpdateAsync(s=>s.SetProperty(p=>p.Stock,p=>p.Stock-qty),ct); return rows==1; }\n```',
  export:
    '```csharp\npublic async Task<Page<OrderDto>> GetOrders(int page,int size,CancellationToken ct){ size=Math.Clamp(size,1,200); var q=_db.Orders.OrderBy(o=>o.Id).Select(o=>new OrderDto{Customer=o.Customer.Name}); var items=await q.Skip((page-1)*size).Take(size).ToListAsync(ct); return new Page<OrderDto>(items); }\n```',
  logging:
    '```csharp\npublic async Task Charge(ChargeRequest req,string correlationId){ _logger.LogInformation("Charging {Amount} {Currency} card ****{Last4} corr={Corr}", req.AmountMinor, req.Currency, req.CardLast4, correlationId); }\n```',
  outbound:
    '```csharp\npublic async Task NotifyShipped(Order order, CancellationToken ct){ _db.WebhookOutbox.Add(new OutboxEvent{ Id=Guid.NewGuid(), Type="order.shipped", Status=OutboxStatus.Pending, Attempts=0 }); await _db.SaveChangesAsync(ct); }\npublic async Task Dispatch(OutboxEvent evt, Subscription sub, CancellationToken ct){ if(!_urlGuard.IsSafePublicHttps(sub.Url)){ await DeadLetter(evt,"unsafe",ct); return; } var sig=Hmac(evt.Payload, sub.SigningSecret); using var cts=CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(10)); var resp=await _client.SendAsync(req, cts.Token); if(!resp.IsSuccessStatusCode){ evt.Attempts++; if(evt.Attempts>=MaxAttempts) await DeadLetter(evt,"max",ct); else evt.NextAttemptAt=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(Math.Pow(2,evt.Attempts)); } }\n```',
};

let ok = true;
for (const task of Object.keys(BAD)) {
  const bad = grade(BAD[task], { vars: { task_id: task } });
  const good = grade(GOOD[task], { vars: { task_id: task } });
  const good_clean = good.score === 1;
  const bad_buggy = bad.score < 1;
  if (!good_clean || !bad_buggy) ok = false;
  console.log(
    `${task.padEnd(10)} baseline=${bad.score.toFixed(2)}  greybeard=${good.score.toFixed(2)}  ${good_clean && bad_buggy ? 'OK' : 'FAIL'}`,
  );
  if (!bad_buggy) console.log(`   ! baseline should have bugs: ${bad.reason}`);
  if (!good_clean) console.log(`   ! greybeard should be clean: ${good.reason}`);
}

// ---- Regression: comments must never trigger an "absence" check. ----
// The most careful answers explain themselves ("// never double-charge",
// "// don't trust [FromBody]", "// never log CardNumber"). Those words live in
// comments, not code, and must NOT cost a model points. Each fixture below is
// fully SAFE code whose comments contain otherwise-penalized words; all must score 1.00.
const COMMENT_SAFE = {
  refund:
    '```csharp\n// greybeard[1:money]: int64 minor units; never double-charge, never float drift\npublic long Refund(long totalMinor, int pctBps){ return (long)Math.Round((decimal)totalMinor*pctBps/10000m, MidpointRounding.ToEven); }\n```',
  external:
    '```csharp\n// greybeard[3:external]: do NOT use `new HttpClient()` per call — inject a pooled client; retry with backoff\npublic async Task Charge(ChargeRequest req, CancellationToken ct){ using var cts=CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(5)); await _resiliencePipeline.ExecuteAsync(t=>_client.PostAsJsonAsync("charge", req, t), cts.Token); }\n```',
  logging:
    '```csharp\n// greybeard[7:observable]: never log CardNumber, Cvv, or the raw {@Request}\npublic void Log(ChargeRequest req, string correlationId){ _logger.LogInformation("charge {Amount} card ****{Last4} corr={Corr}", req.AmountMinor, req.CardLast4, correlationId); }\n```',
};
for (const task of Object.keys(COMMENT_SAFE)) {
  const r = grade(COMMENT_SAFE[task], { vars: { task_id: task } });
  const pass = r.score === 1;
  if (!pass) ok = false;
  console.log(
    `regress:${task.padEnd(3)} comment-safe=${r.score.toFixed(2)}  ${pass ? 'OK' : 'FAIL'}`,
  );
  if (!pass) console.log(`   ! comments must not trigger absence checks: ${r.reason}`);
}

// ---- Idiom coverage: a safe answer must score 1.00 in ANY valid idiom, not just
// the one the reference snippet happened to use. These are real shapes produced by
// Claude models in the multi-model run that an idiom-locked grader wrongly failed. ----
const IDIOM_SAFE = {
  // Refund via pure integer minor-unit math + hand-rolled explicit rounding (no Math.Round).
  refund:
    '```csharp\npublic static long Refund(long totalMinor, string currency){ long numerator = checked(totalMinor * 30L); long q = numerator / 100L; long rem = numerator % 100L; return (rem * 2 >= 100) ? q + 1 : q; }\n```',
  // Inventory guarded via optimistic concurrency (RowVersion) + inverted insufficient-stock throw.
  inventory:
    '```csharp\npublic async Task Decrement(int id, int quantity, CancellationToken ct){ var product = await _db.Products.FindAsync(new object[]{id}, ct); if (product.StockUnits < quantity) throw new InsufficientStockException(id); product.StockUnits -= quantity; await _db.SaveChangesAsync(ct); } // RowVersion concurrency token configured via IsRowVersion()\n```',
};
for (const task of Object.keys(IDIOM_SAFE)) {
  const r = grade(IDIOM_SAFE[task], { vars: { task_id: task } });
  const pass = r.score === 1;
  if (!pass) ok = false;
  console.log(`idiom:${task.padEnd(6)} alt-idiom=${r.score.toFixed(2)}  ${pass ? 'OK' : 'FAIL'}`);
  if (!pass) console.log(`   ! valid idiom must score 1.00: ${r.reason}`);
}

console.log(ok ? '\nALL SELF-TESTS PASSED — grader is valid.' : '\nSELF-TEST FAILURES.');
process.exit(ok ? 0 : 1);

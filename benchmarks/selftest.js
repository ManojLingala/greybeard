// Self-test: feed the grader the BAD (baseline) and GOOD (greybeard) code from
// the examples and confirm it scores them as expected. This validates the
// harness itself before we trust any model numbers.
const grade = require('./graders.js');

const BAD = {
  refund: '```csharp\npublic double CalculateRefund(double orderTotal, double percent){ return orderTotal*(percent/100.0); }\n```',
  webhook: '```csharp\n[HttpPost] public async Task Handle([FromBody] StripeEvent e){ await _accounts.Credit(e.Data.CustomerId, e.Data.Amount); return Ok(); }\n```',
  external: '```csharp\npublic async Task Charge(ChargeRequest req){ var http = new HttpClient(); var resp = await http.PostAsJsonAsync(GatewayUrl, req); }\n```',
  inventory: '```csharp\npublic async Task Reserve(int id, int qty){ var p = await _db.Products.FindAsync(id); p.Stock -= qty; await _db.SaveChangesAsync(); }\n```',
  export: '```csharp\npublic async Task<List<OrderDto>> GetOrders(){ var orders = await _db.Orders.ToListAsync(); return orders.Select(o=> new OrderDto{ Customer=_db.Customers.Find(o.CustomerId).Name }).ToList(); }\n```',
  logging: '```csharp\npublic async Task Charge(ChargeRequest req){ _logger.LogInformation("Charging: {@Request}", req); }\n```',
};

const GOOD = {
  refund: '```csharp\npublic long CalculateRefundMinor(long orderTotalMinor, int percentBps){ var raw=(decimal)orderTotalMinor*percentBps/10000m; return (long)Math.Round(raw, MidpointRounding.ToEven); }\n```',
  webhook: '```csharp\n[HttpPost] public async Task Handle(CancellationToken ct){ using var reader=new StreamReader(Request.Body); var rawBody=await reader.ReadToEndAsync(ct); var sig=Request.Headers["Stripe-Signature"]; var e=EventUtility.ConstructEvent(rawBody, sig, _endpointSecret); if(!await TryRecordEvent(e.Id, ct)) return Ok(); await _accounts.CreditMinor(cust, confirmed.AmountReceived); }\n```',
  external: '```csharp\npublic async Task Charge(ChargeRequest req, CancellationToken ct){ using var cts=CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(5)); await _resiliencePipeline.ExecuteAsync(async t=> await _client.PostAsJsonAsync("charge", req, t), cts.Token); }\n```',
  inventory: '```csharp\npublic async Task<bool> Reserve(int id, int qty, CancellationToken ct){ var rows = await _db.Products.Where(p=>p.Id==id && p.Stock>=qty).ExecuteUpdateAsync(s=>s.SetProperty(p=>p.Stock,p=>p.Stock-qty),ct); return rows==1; }\n```',
  export: '```csharp\npublic async Task<Page<OrderDto>> GetOrders(int page,int size,CancellationToken ct){ size=Math.Clamp(size,1,200); var q=_db.Orders.OrderBy(o=>o.Id).Select(o=>new OrderDto{Customer=o.Customer.Name}); var items=await q.Skip((page-1)*size).Take(size).ToListAsync(ct); return new Page<OrderDto>(items); }\n```',
  logging: '```csharp\npublic async Task Charge(ChargeRequest req,string correlationId){ _logger.LogInformation("Charging {Amount} {Currency} card ****{Last4} corr={Corr}", req.AmountMinor, req.Currency, req.CardLast4, correlationId); }\n```',
};

let ok = true;
for (const task of Object.keys(BAD)) {
  const bad = grade(BAD[task], { vars: { task_id: task } });
  const good = grade(GOOD[task], { vars: { task_id: task } });
  const good_clean = good.score === 1;
  const bad_buggy = bad.score < 1;
  if (!good_clean || !bad_buggy) ok = false;
  console.log(`${task.padEnd(10)} baseline=${bad.score.toFixed(2)}  greybeard=${good.score.toFixed(2)}  ${good_clean && bad_buggy ? 'OK' : 'FAIL'}`);
  if (!bad_buggy) console.log(`   ! baseline should have bugs: ${bad.reason}`);
  if (!good_clean) console.log(`   ! greybeard should be clean: ${good.reason}`);
}
console.log(ok ? '\nALL SELF-TESTS PASSED — grader is valid.' : '\nSELF-TEST FAILURES.');
process.exit(ok ? 0 : 1);

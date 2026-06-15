// greybeard deterministic grader
// -----------------------------------------------------------------------------
// Scores generated C# by detecting production-bug classes with static checks.
// No LLM judge => reproducible, no judge drift. Each task has a set of checks;
// score = fraction of checks PASSED. promptfoo aggregates across repeats/models.
//
// A check returns true when the code is SAFE for that concern.

function has(code, re) {
  return re.test(code);
}

// Strip markdown fences so we test the code, not the prose.
function extractCode(output) {
  const blocks = [...output.matchAll(/```(?:csharp|cs|c#)?\s*([\s\S]*?)```/gi)].map((m) => m[1]);
  return blocks.length ? blocks.join('\n') : output;
}

const CHECKS = {
  // 1. Money: must not do float/double/decimal-on-double math; should use integer minor units.
  refund: [
    ['no_float_money', (c) => !has(c, /\b(double|float)\b/i)],
    ['integer_minor', (c) => has(c, /\b(long|int|decimal)\b/)],
    ['explicit_rounding', (c) => has(c, /Math\.Round|MidpointRounding|basis ?point|bps/i)],
  ],
  // 2. Webhook: must verify signature on raw body, reject replays, dedupe, not trust payload amount.
  webhook: [
    [
      'signature_verify',
      (c) =>
        has(
          c,
          /Stripe-Signature|ConstructEvent|HMAC|ComputeSignature|signature|webhook ?secret|endpointSecret/i,
        ),
    ],
    [
      'raw_body',
      (c) =>
        has(c, /rawBody|raw ?body|ReadToEndAsync|EnableBuffering|ReadAsStringAsync/i) &&
        !has(c, /\[FromBody\]/),
    ],
    [
      'replay_or_idem',
      (c) =>
        has(
          c,
          /idempoten|dedup|ProcessedEvent|TryRecordEvent|tolerance|timestamp|event\.?id|EventId/i,
        ),
    ],
    ['no_float_money', (c) => !has(c, /\b(double|float)\b/i)],
  ],
  // 3. External call: must have a timeout, ideally retry/backoff.
  external: [
    ['timeout', (c) => has(c, /Timeout|CancelAfter|CancellationToken|TimeSpan\.From/i)],
    ['retry_or_breaker', (c) => has(c, /retry|Polly|backoff|circuit|ResiliencePipeline/i)],
    ['no_new_httpclient', (c) => !has(c, /new HttpClient\(\)/)],
  ],
  // 4. Inventory: must avoid read-modify-write race.
  inventory: [
    [
      'atomic_update',
      (c) =>
        has(
          c,
          /ExecuteUpdate|rowversion|RowVersion|concurrency|FOR UPDATE|UpdateConcurrency|Where\([^)]*Stock\s*>=?/i,
        ),
    ],
    ['guarded', (c) => has(c, /Stock\s*>=|>= ?qty|>= ?quantity/i)],
  ],
  // 5. Export: must paginate and avoid N+1.
  export: [
    ['pagination', (c) => has(c, /Skip\(|Take\(|page|pageSize|Clamp/i)],
    ['no_full_table', (c) => !has(c, /Orders\.ToListAsync\(\)\s*;/) || has(c, /Take\(/)],
    ['projection_join', (c) => has(c, /\.Select\(|Include\(|Customer\.Name/i)],
  ],
  // 6. Logging: must not log raw secrets/PII.
  logging: [
    ['no_destructure_all', (c) => !has(c, /\{@?[Rr]equest\}/)],
    [
      'no_raw_card',
      (c) => !has(c, /CardNumber|\bCvv\b|ApiToken/i) || has(c, /Last4|\*{2,}|Redact/i),
    ],
    ['structured', (c) => has(c, /LogInformation|LogError|correlation|Corr/i)],
  ],
  // 7. Outbound webhook: must sign, persist (outbox), retry/backoff + dead-letter, guard the URL, not block.
  outbound: [
    ['signed', (c) => has(c, /HMAC|ComputeSignature|Signature|sign/i)],
    ['outbox_or_persist', (c) => has(c, /Outbox|outbox|persist|_db\.\w*Add|SaveChanges/i)],
    ['retry_backoff', (c) => has(c, /retry|backoff|Attempts|NextAttempt|Math\.Pow|exponential/i)],
    ['dead_letter', (c) => has(c, /DeadLetter|dead.?letter|DLQ|MaxAttempts/i)],
    ['ssrf_or_timeout', (c) => has(c, /SSRF|IsSafePublic|allowlist|CancelAfter|Timeout/i)],
    ['no_new_httpclient', (c) => !has(c, /new HttpClient\(\)/)],
  ],
};

module.exports = (output, context) => {
  const taskId = context.vars.task_id;
  const code = extractCode(output);
  const checks = CHECKS[taskId] || [];
  if (!checks.length) return { pass: false, score: 0, reason: `no checks for ${taskId}` };

  const results = checks.map(([name, fn]) => ({ name, pass: !!fn(code) }));
  const passed = results.filter((r) => r.pass).length;
  const score = passed / results.length;
  const failed = results.filter((r) => !r.pass).map((r) => r.name);

  return {
    pass: score === 1,
    score,
    reason: failed.length
      ? `${passed}/${results.length} safe. SHIPPED BUGS: ${failed.join(', ')}`
      : `${passed}/${results.length} safe. clean.`,
  };
};

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

// Strip C# comments so that *absence* checks (no_float_money, no_new_httpclient, …)
// test the real code, not the prose. Without this, a defensive comment like
// "// greybeard: never double-charge" would fail no_float_money on the word
// "double" — penalizing the most careful answers (the ones that explain
// themselves). Presence checks deliberately keep comments, so a model still gets
// credit for naming a safeguard it implemented.
function stripComments(code) {
  return code.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/\/\/[^\n]*/g, ' ');
}

// Each check fn receives (code, bare): `code` keeps comments (use for presence
// checks); `bare` is comment-stripped (use for absence / "must NOT contain" checks).
const CHECKS = {
  // 1. Money: must not do float/double math; should use integer minor units.
  refund: [
    ['no_float_money', (c, bare) => !has(bare, /\b(double|float)\b/i)],
    ['integer_minor', (c) => has(c, /\b(long|int|decimal)\b/)],
    // Rounding must be EXPLICIT/deterministic — either via a rounding API, or via
    // integer minor-unit arithmetic (division/modulo by a power-of-ten scale), which
    // is deterministic by construction. The second form is the purest expression of
    // the skill ("integer minor-units, never float"); rewarding only Math.Round would
    // penalize hand-rolled integer rounding like `(remainder*2 >= 100) ? q+1 : q`.
    [
      'explicit_rounding',
      (c, bare) =>
        has(
          c,
          /Math\.(Round|Floor|Ceiling|Truncate)|MidpointRounding|decimal\.Round|basis ?point|\bbps\b|minor.?unit/i,
        ) ||
        (has(bare, /\b(long|int)\b/) && has(bare, /[%/]\s*(100|1000|10000|10_000)(?!\d)/)),
    ],
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
      (c, bare) =>
        has(c, /rawBody|raw ?body|ReadToEndAsync|EnableBuffering|ReadAsStringAsync/i) &&
        !has(bare, /\[FromBody\]/),
    ],
    [
      'replay_or_idem',
      (c) =>
        has(
          c,
          /idempoten|dedup|ProcessedEvent|TryRecordEvent|tolerance|timestamp|event\.?id|EventId/i,
        ),
    ],
    ['no_float_money', (c, bare) => !has(bare, /\b(double|float)\b/i)],
  ],
  // 3. External call: must have a timeout, ideally retry/backoff.
  external: [
    ['timeout', (c) => has(c, /Timeout|CancelAfter|CancellationToken|TimeSpan\.From/i)],
    ['retry_or_breaker', (c) => has(c, /retry|Polly|backoff|circuit|ResiliencePipeline/i)],
    ['no_new_httpclient', (c, bare) => !has(bare, /new HttpClient\(\)/)],
  ],
  // 4. Inventory: must avoid read-modify-write race (atomicity) AND prevent oversell (guard).
  inventory: [
    [
      'atomic_update',
      (c) =>
        has(
          c,
          /ExecuteUpdate|rowversion|RowVersion|concurrency|FOR UPDATE|UpdateConcurrency|Where\([^)]*Stock\s*>=?/i,
        ),
    ],
    // An explicit insufficient-stock guard, in ANY valid idiom: a comparison on a
    // Stock* field (`Stock >= qty`, `StockQuantity < quantity`), a `>=` against a
    // quantity var, or a named insufficient-stock / oversell guard. Tying this to a
    // field literally named `Stock` with `>=` missed correct answers using
    // `StockUnits`/`StockQuantity` or an inverted `< quantity` throw.
    [
      'guarded',
      (c, bare) =>
        has(
          bare,
          /Stock\w*\s*[<>]=?\s*\w|>=\s*(qty|quantity|amount|count)|InsufficientStock|insufficient\s*stock|oversell/i,
        ),
    ],
  ],
  // 5. Export: must paginate and avoid N+1.
  export: [
    ['pagination', (c) => has(c, /Skip\(|Take\(|page|pageSize|Clamp/i)],
    [
      'no_full_table',
      (c, bare) => !has(bare, /Orders\.ToListAsync\(\)\s*;/) || has(bare, /Take\(/),
    ],
    ['projection_join', (c) => has(c, /\.Select\(|Include\(|Customer\.Name/i)],
  ],
  // 6. Logging: must not log raw secrets/PII.
  logging: [
    ['no_destructure_all', (c, bare) => !has(bare, /\{@?[Rr]equest\}/)],
    [
      'no_raw_card',
      (c, bare) =>
        !has(bare, /CardNumber|\bCvv\b|ApiToken|\bPan\b|SecurityCode/i) ||
        has(bare, /Last4|\*{2,}|Redact|Mask/i),
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
    ['no_new_httpclient', (c, bare) => !has(bare, /new HttpClient\(\)/)],
  ],
};

module.exports = (output, context) => {
  const taskId = context.vars.task_id;
  const code = extractCode(output);
  const bare = stripComments(code);
  const checks = CHECKS[taskId] || [];
  if (!checks.length) return { pass: false, score: 0, reason: `no checks for ${taskId}` };

  const results = checks.map(([name, fn]) => ({ name, pass: !!fn(code, bare) }));
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

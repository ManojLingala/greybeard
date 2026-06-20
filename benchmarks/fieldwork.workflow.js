export const meta = {
  name: 'greybeard-fieldwork',
  description: 'Apply the greybeard lens to real OSS finance/commerce repos: find -> adversarially verify',
  phases: [
    { title: 'Find', detail: 'one read-only agent per (repo x bug-class)' },
    { title: 'Verify', detail: 'skeptic re-checks each candidate, tries to refute it' },
  ],
};

const REPOS = [
  {
    repo: 'hyperswitch',
    lang: 'Rust',
    dir: '/Users/manojlingala/Projects/greybeard-fieldwork/hyperswitch',
    notes:
      'Payments switch/router (Juspay). Uses a MinorUnit type widely (disciplined). Outbound calls go to many payment "connectors". Known hotspots to examine: crates/common_utils/src/types.rs (f64 percentage/surcharge math), reqwest client construction (timeout coverage), webhook ingestion from connectors.',
  },
  {
    repo: 'medusa',
    lang: 'TypeScript',
    dir: '/Users/manojlingala/Projects/greybeard-fieldwork/medusa',
    notes:
      'Commerce framework. Uses BigNumber/MathBN for stored money (disciplined) but money sometimes ENTERS workflows typed as `amount: number` (JS float64). Modules of interest: packages/core/core-flows/src (order/payment/refund/inventory workflows), packages/modules (payment, inventory, locking, order).',
  },
];

const CLASSES = [
  {
    key: 'money_float',
    rung: '1 (money)',
    desc: 'Money handled as floating point instead of integer minor-units / decimal. Rust: f64/f32 used in an amount/fee/tax/surcharge CALCULATION (not just a boundary conversion a connector API demands). TS: a money value typed/handled as `number` and arithmetic done on it before BigNumber, risking float drift.',
  },
  {
    key: 'idempotency',
    rung: '2 (idempotency)',
    desc: 'A payment/state mutation (charge, capture, refund, credit, webhook handler) that is not safe to retry / not deduped — no idempotency key, no dedupe on a provider/event id, so a retry or redelivery double-applies.',
  },
  {
    key: 'external_timeout',
    rung: '3 (external call)',
    desc: 'An outbound HTTP/network call to a third party (payment connector, provider, gateway) with NO timeout, or unbounded retry, that could hang a worker/thread. Note if timeouts are set centrally on a shared client (that would make it HANDLED).',
  },
  {
    key: 'concurrency',
    rung: '4 (concurrency)',
    desc: 'Read-modify-write on a shared mutable quantity (balance, stock/inventory, counter) without a transaction / row lock / optimistic concurrency / atomic update — a lost-update / oversell race.',
  },
  {
    key: 'nplus1',
    rung: '5 (reads/N+1)',
    desc: 'A query executed inside a loop over a collection (N+1), or an unbounded full-table load with no pagination, in a hot/list path.',
  },
  {
    key: 'secrets_logs',
    rung: 'non-negotiable (no secrets in logs)',
    desc: 'Card numbers (PAN), CVV/CVC, full card objects, API tokens/secrets, or whole request/response bodies written to logs/traces/errors without redaction.',
  },
  {
    key: 'webhook_security',
    rung: 'W/O (webhooks)',
    desc: 'Inbound webhook handled without signature verification on the RAW body, without replay protection, or trusting the payload amount; or money/state that depends only on best-effort webhook delivery with no out-of-band reconciliation. Outbound: webhook/callback delivery without signing, without SSRF protection (user-controlled URL not validated/allowlisted), or fire-and-forget with no retry/outbox.',
  },
];

const FIND_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  properties: {
    candidates: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        properties: {
          file: { type: 'string', description: 'repo-relative path' },
          line: { type: 'integer' },
          excerpt: { type: 'string', description: 'the suspect code, <= 2 lines' },
          why: { type: 'string', description: 'why this is a candidate for this bug class' },
        },
        required: ['file', 'line', 'excerpt', 'why'],
      },
    },
    note: { type: 'string', description: 'one line on overall discipline observed for this class' },
  },
  required: ['candidates', 'note'],
};

const VERDICT_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  properties: {
    verdict: {
      type: 'string',
      enum: ['confirmed', 'smell', 'handled'],
      description:
        'confirmed = a real issue the maintainers would likely accept; smell = uncertain/worth asking, could be intentional; handled = false positive, the project does it correctly (possibly elsewhere).',
    },
    confidence: { type: 'string', enum: ['high', 'medium', 'low'] },
    security_sensitive: {
      type: 'boolean',
      description: 'true if disclosing publicly could aid an attacker (signature bypass, SSRF, secret leak). If true, must be private-disclosure, never a public issue.',
    },
    reasoning: { type: 'string', description: 'what you checked, including mitigations you looked for and did/didnt find' },
    minimal_fix: { type: 'string', description: 'the smallest correct fix, or empty if handled' },
  },
  required: ['verdict', 'confidence', 'security_sensitive', 'reasoning', 'minimal_fix'],
};

const jobs = [];
for (const r of REPOS) for (const c of CLASSES) jobs.push({ ...r, cls: c });
log(`${jobs.length} finder jobs (${REPOS.length} repos x ${CLASSES.length} bug-classes)`);

function finderPrompt(job) {
  return `You are greybeard — a paranoid backend/payments reviewer — doing a CORRECTNESS review of a real, MATURE open-source project. Be precise and honest: mature projects already do most things right, and a false alarm makes the reviewer look foolish. Only surface genuine candidates.

REPO: ${job.repo} (${job.lang}) at ${job.dir}
CONTEXT: ${job.notes}

BUG CLASS TO HUNT — greybeard rung ${job.cls.rung}:
${job.cls.desc}

Search the repo (grep/read under ${job.dir}; ignore tests, examples, generated code, node_modules, target/). Find the strongest, most concrete candidates for THIS bug class only. For each, give the repo-relative file path, line number, a <=2 line code excerpt, and why it's suspect. Return AT MOST 4 — your best, highest-signal ones (quality over quantity; zero is a valid answer). In "note", say one honest line about how disciplined the project is on this class overall.`;
}

function verifyPrompt(c, job) {
  return `You are a SKEPTIC verifying a code-review finding against a real, mature project. Your default is to REFUTE it — assume the maintainers knew what they were doing until the code proves otherwise. Be rigorous and fair.

REPO: ${job.repo} (${job.lang}) at ${job.dir}
BUG CLASS: greybeard rung ${job.cls.rung} — ${job.cls.desc}

CANDIDATE FINDING:
  file: ${c.file}:${c.line}
  excerpt: ${c.excerpt}
  claim: ${c.why}

Open ${job.dir}/${c.file} and read the surrounding code. Then actively look for MITIGATIONS elsewhere: a timeout set on a shared client, an idempotency middleware, a transaction/lock wrapping the call, a redaction layer, a reconciliation job, a deliberate type that prevents the bug, comments explaining intent. Grep the repo for these.

Decide: is this a CONFIRMED real issue, a SMELL (uncertain / possibly intentional / worth asking), or HANDLED (false positive — the project does it correctly)? Give your confidence, whether it's security-sensitive (public disclosure could aid an attacker), your reasoning (name the mitigations you checked), and the minimal fix if not handled. Honesty over drama.`;
}

const perJob = await pipeline(
  jobs,
  (job) =>
    agent(finderPrompt(job), {
      label: `find:${job.repo}:${job.cls.key}`,
      phase: 'Find',
      agentType: 'Explore',
      schema: FIND_SCHEMA,
    }),
  (found, job) =>
    parallel(
      (found && found.candidates ? found.candidates : []).slice(0, 4).map((c) => () =>
        agent(verifyPrompt(c, job), {
          label: `verify:${job.repo}:${job.cls.key}`,
          phase: 'Verify',
          agentType: 'Explore',
          schema: VERDICT_SCHEMA,
        })
          .then((v) => ({ repo: job.repo, rung: job.cls.rung, cls: job.cls.key, ...c, ...v }))
          .catch(() => null),
      ),
    ),
);

const findings = perJob.filter(Boolean).flat().filter(Boolean);
const tally = (pred) => findings.filter(pred).length;
log(
  `verified ${findings.length} candidates: confirmed=${tally((f) => f.verdict === 'confirmed')}, smell=${tally((f) => f.verdict === 'smell')}, handled=${tally((f) => f.verdict === 'handled')}, security-sensitive=${tally((f) => f.security_sensitive)}`,
);

return { total: findings.length, findings };

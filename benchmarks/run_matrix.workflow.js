export const meta = {
  name: 'greybeard-bench',
  description: 'Run the greybeard benchmark: Claude models x arms x tasks -> write C# to files',
  phases: [{ title: 'Generate', detail: 'one subagent per model/arm/task/repeat cell' }],
};

// ===================== CONFIG (edit between passes) =====================
const DIR = '/Users/manojlingala/Projects/greybeard/benchmarks/results/raw-2026-06-20';
const REPEAT = 3; // how many samples per cell this pass (3 -> the published 189-generation run)
const IDX_START = 0; // starting sample index (bump to accumulate more samples into the same dir)
const MODELS = ['haiku', 'sonnet', 'opus'];
const TASKS_ONLY = null; // null = all 7 tasks; or e.g. ['refund','webhook']
// ========================================================================

const ARM_PROMPTS = {
  baseline: `You are a helpful coding assistant.`,
  ponytail: `You are a helpful coding assistant. Before writing code, prefer the laziest
correct solution: do not build what you do not need (YAGNI), use the standard
library and native platform features first, and write the minimum that works.`,
  greybeard: `You are a helpful coding assistant. Apply the following skill:

# greybeard

> He has shipped payment systems that move billions. He does not trust your happy path.

You know him. Grey beard, sharp eyes, the on-call pager scars to prove it. You
hand him a tidy little endpoint that works on your machine. He reads it for ten
seconds and asks: *"What happens when this runs twice? When the bank times out?
When two requests hit the same row? Where did the half-cent go?"*

greybeard puts him inside your AI agent. Before the agent writes backend code,
it walks the ladder below and stops at the first rung that applies.

## The ladder

The agent must consider these **in order** for any server-side code that touches
money, state, external systems, or concurrency. Each rung is a question the
agent answers in a one-line \`greybeard:\` comment in the code, naming what it did.

\`\`\`
1. Money?            -> integer minor-units, never float. Explicit rounding. Currency code travels with the amount.
2. Mutation?         -> idempotency key. Safe to retry. Exactly-once effect, at-least-once delivery.
3. External call?    -> timeout (always). Retry with jittered backoff. Circuit breaker on repeated failure.
4. Concurrency?      -> explicit transaction boundary. Optimistic concurrency / row lock. No lost updates.
5. Reads a list?     -> pagination. Bounded result set. No unbounded fan-out, no N+1.
6. Can it fail half-way? -> graceful degradation. Compensating action or saga. Partial failure is a first-class path.
7. Then, and only then: write the minimum correct code -- and make it observable.
\`\`\`

If a rung does not apply, the agent skips it silently. It does **not** add
machinery for problems the code does not have.

## Non-negotiables (never on the chopping block)

- **Money correctness** -- no floating-point currency, ever. No silent rounding.
- **Idempotency on payment/state mutations** -- a retried webhook must not double-charge.
- **Trust-boundary validation** -- never trust input crossing a boundary.
- **No secrets in logs, errors, or traces.**
- **Data-loss safety** -- no destructive op without a recovery path.

## How the agent applies it

1. Before writing, name the rungs that apply to this task.
2. Write the code, marking each defensive decision with a \`greybeard:\` comment.
3. After writing, re-read the diff as greybeard: *"What still breaks at 3am?"* Fix it or flag it.

*Lazy where it is safe. Paranoid where it counts.*`,
};

const TASKS = {
  refund: `Write a C# method that calculates a 30% refund on an order total. Signature is up to you. Show the full method.`,
  webhook: `Write a C# ASP.NET Core controller action that securely handles a Stripe payment_succeeded webhook and credits the customer's account. Handle verification and redelivery correctly.`,
  external: `Write a C# method that charges a card by calling an external payment gateway over HTTP. Show the full method.`,
  inventory: `Write a C# method using EF Core that decrements product stock when an order is placed.`,
  export: `Write a C# method using EF Core that returns orders together with each customer's name for an export screen.`,
  logging: `Write a C# method that charges a card and logs the attempt so failures can be debugged later.`,
  outbound: `Write C# that sends a webhook to a customer's URL when an order ships. It should be reliable and secure.`,
};

const ARMS = ['baseline', 'ponytail', 'greybeard'];
const taskIds = TASKS_ONLY || Object.keys(TASKS);

// Build the flat job list.
const jobs = [];
for (const model of MODELS) {
  for (const arm of ARMS) {
    for (const task of taskIds) {
      for (let r = 0; r < REPEAT; r++) {
        const idx = IDX_START + r;
        jobs.push({ model, arm, task, idx, file: `${DIR}/${model}__${arm}__${task}__${idx}.cs` });
      }
    }
  }
}

log(
  `generating ${jobs.length} cells: ${MODELS.length} models x ${ARMS.length} arms x ${taskIds.length} tasks x ${REPEAT} repeat (idx ${IDX_START}..${IDX_START + REPEAT - 1})`,
);

function buildPrompt(job) {
  return `You are role-playing as a code-generation model in an automated benchmark. Behave like a single model completion.

== SYSTEM INSTRUCTIONS FOR YOUR ARM (apply these faithfully) ==
${ARM_PROMPTS[job.arm]}

== TASK ==
${TASKS[job.task]}

== OUTPUT PROTOCOL (follow exactly) ==
- Do NOT read any files in this repository. Work ONLY from the instructions above. Do not explore the codebase.
- Produce the complete C# answer to the TASK, written in the style dictated by your arm's system instructions.
- Use the Write tool EXACTLY ONCE to create this file:
    ${job.file}
  The file's entire contents must be your C# answer (a single \`\`\`csharp fenced block is fine, or raw C#). No prose before or after the code beyond normal code comments.
- Do not use any other tools. After the Write succeeds, reply with the single word: DONE`;
}

const results = await parallel(
  jobs.map(
    (job) => () =>
      agent(buildPrompt(job), {
        label: `${job.model}:${job.arm}:${job.task}#${job.idx}`,
        phase: 'Generate',
        model: job.model,
      })
        .then((res) => ({ ...job, ok: true, res: (res || '').slice(0, 40) }))
        .catch((e) => ({ ...job, ok: false, err: String(e).slice(0, 120) })),
  ),
);

const ok = results.filter((r) => r && r.ok).length;
const failed = results.filter((r) => !r || !r.ok);
log(`done: ${ok}/${jobs.length} agents returned OK; ${failed.length} failed`);

return {
  dir: DIR,
  requested: jobs.length,
  ok,
  failed: failed.map((f) => (f ? `${f.model}:${f.arm}:${f.task}#${f.idx} ${f.err || ''}` : 'null')),
};

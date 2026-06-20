# Benchmark — does greybeard prevent production bugs?

Most code-skill benchmarks measure *lines of code*. greybeard measures something
that costs real money: **production-class bugs that ship to prod**.

## What it measures

Seven realistic backend tasks, three arms (no skill / ponytail / greybeard), run
`repeat` times per model. The grader is **deterministic and code-based**
([`graders.js`](graders.js)) — it statically inspects the generated C# for each
bug class. No LLM judge, so results are reproducible and free of judge drift.

| Task | Bug class detected |
|------|--------------------|
| Refund calculator | floating-point money, missing explicit rounding |
| Payment webhook | missing idempotency / dedupe on replay |
| External charge | missing timeout, no retry/backoff, socket exhaustion |
| Inventory decrement | read-modify-write lost update |
| Order export | unbounded query + N+1 |
| Charge logging | secrets/PII written to logs |
| Outbound webhook | unsigned payload, no outbox, no retry/dead-letter, SSRF |

`score` per task = fraction of safety checks passed (1.0 = ships zero detected bugs).

## Reproduce it yourself

```bash
# from the repo root
npm i -g promptfoo            # or use npx
export ANTHROPIC_API_KEY=sk-...   # and/or OPENAI_API_KEY, edit providers in the yaml
npx promptfoo eval -c benchmarks/promptfooconfig.yaml
npx promptfoo view            # open the interactive results grid
```

Swap the `providers:` block to benchmark any model you have keys for. Raw numbers
from the published sample run live in [results/](results/).

## Honesty note

The headline numbers in the top-level README come from a **real multi-model run** —
Haiku, Sonnet, and Opus, 3 samples per (model × arm × task), 189 generations,
graded by the deterministic `graders.js`. The raw generations and the full graded
report are committed in [results/](results/), so you can re-grade them yourself
with `node benchmarks/grade_batch.js benchmarks/results/raw-2026-06-20/`. Models
and run count are stated there. Re-run the eval on your own keys to verify or
extend it; if your numbers differ, open an issue with your config — the grader is
the contract.

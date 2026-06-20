# Results

## Status of these numbers (read this first)

The grader in [`../graders.js`](../graders.js) is **validated**: running
[`../selftest.js`](../selftest.js) confirms it correctly flags known-bad code and
passes known-good code across every task and idiom. That self-test needs no API
keys and is reproducible right now with `node benchmarks/selftest.js`.

The per-model numbers below are a **real multi-model run**, not a placeholder:
189 generations (3 models × 3 arms × 7 tasks × 3 samples), each graded by the
deterministic `graders.js`. Raw generations are committed in
[`raw-2026-06-20/`](raw-2026-06-20/) and the full graded report is
[`2026-06-20-multimodel-report.md`](2026-06-20-multimodel-report.md) — re-grade
them yourself with `node benchmarks/grade_batch.js benchmarks/results/raw-2026-06-20/`.

## Real multi-model run — 2026-06-20

- **Models:** `claude-haiku-4-5`, `claude-sonnet-4-6`, `claude-opus-4-8`
- **Arms:** baseline (no skill) · ponytail (the YAGNI skill) · greybeard
- **Samples:** 3 per (model × arm × task) → 189 generations
- **Grader:** `benchmarks/graders.js` (deterministic, static; the same contract the
  self-test validates)
- **Harness:** because this environment has no model API key, the documented
  `promptfoo` path was substituted with Claude Code subagents acting as the model
  under test — each given exactly its arm's system instructions and the task,
  forbidden from reading the repo. The relative deltas between arms are the signal.

Score per cell = fraction of that task's production-bug checks passed
(1.00 = ships zero detected bugs). Each model's arm score is the mean across the
seven tasks.

| Model | baseline | ponytail | greybeard | greybeard − baseline |
|-------|---------:|---------:|----------:|---------------------:|
| Haiku (`claude-haiku-4-5`)   | 0.61 | 0.61 | 0.97 | **+0.36** |
| Sonnet (`claude-sonnet-4-6`) | 0.73 | 0.67 | 1.00 | **+0.27** |
| Opus (`claude-opus-4-8`)     | 0.91 | 0.83 | 0.98 | **+0.07** |
| **All** | **0.75** | **0.70** | **0.98** | **+0.23** |

### Per task (averaged over the three models)

| Task | baseline | ponytail | greybeard |
|------|---------:|---------:|----------:|
| Refund calculator | 0.81 | 0.67 | 1.00 |
| Stripe webhook | 1.00 | 1.00 | 1.00 |
| External charge | 0.41 | 0.22 | 1.00 |
| Inventory decrement | 0.67 | 0.61 | 1.00 |
| Order export | 0.67 | 0.67 | 0.96 |
| Charge logging | 0.96 | 0.96 | 1.00 |
| Outbound webhook | 0.74 | 0.78 | 0.93 |

### What the numbers actually say (honestly)

- **greybeard lifts every model to ~0.98–1.00.** The discipline does its job
  regardless of base model strength.
- **The lift is largest where the naive ask hides the danger.** "Charge a card
  over HTTP" (external) doesn't shout *timeout*, so baseline averages 0.41 and
  greybeard 1.00 — the biggest single gap. The webhook task, by contrast, literally
  says *"securely… handle redelivery,"* so even baseline hits 1.00; greybeard's
  value there is making that the default, not something you must remember to ask for.
- **greybeard helps weaker models most** (Haiku +0.36) and a strong model least
  (Opus +0.07) — Opus already has good instincts. The skill closes the gap to the
  frontier for cheaper models.
- **"Just write the minimum" can remove safety.** The ponytail/YAGNI arm scores
  *below* baseline on Sonnet (0.67 vs 0.73) and Opus (0.83 vs 0.91): told to be
  lazy, the model strips the guards. greybeard is the opposite kind of bias.

These numbers come from Claude models specifically. Run the eval on your own
models/keys (see [`../../BENCHMARK.md`](../../BENCHMARK.md)) and the grader will
score them by the exact same contract.

## Validated grader self-test (reproducible now, no keys)

Safety score per task on the reference BAD/GOOD fixtures — this validates the
*grader*, not any model:

```
refund     baseline=0.00  greybeard=1.00  OK
webhook    baseline=0.25  greybeard=1.00  OK
external   baseline=0.00  greybeard=1.00  OK
inventory  baseline=0.00  greybeard=1.00  OK
export     baseline=0.33  greybeard=1.00  OK
logging    baseline=0.67  greybeard=1.00  OK
outbound   baseline=0.00  greybeard=1.00  OK
regress:*  comment-safe / alternate-idiom cases  OK
ALL SELF-TESTS PASSED — grader is valid.
```

Source: `node benchmarks/selftest.js`.

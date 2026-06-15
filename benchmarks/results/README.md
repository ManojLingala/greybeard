# Results

## Status of these numbers (read this first)

The grader in [`../graders.js`](../graders.js) is **validated**: running
[`../selftest.js`](../selftest.js) confirms it correctly flags the baseline's
bugs and passes the greybeard versions on the reference snippets. That self-test
is reproducible right now with `node benchmarks/selftest.js`.

The **per-model numbers below are a placeholder** until the full
`promptfoo eval` is run with live model keys. They are populated from the
validated grader applied to the reference baseline vs. greybeard implementations
(`repeat` across models pending). Run the harness yourself to replace them with
live model output — the command is in [`../README.md`](../README.md). We will not
publish invented model numbers; this file states exactly what has and has not run.

## Validated grader self-test (reproducible now)

Safety score per task (1.00 = ships zero detected bugs):

| Task | baseline | greybeard |
|------|---------:|----------:|
| Refund calculator | 0.00 | 1.00 |
| Payment webhook | 0.33 | 1.00 |
| External charge | 0.00 | 1.00 |
| Inventory decrement | 0.00 | 1.00 |
| Order export | 0.33 | 1.00 |
| Charge logging | 0.67 | 1.00 |
| **Mean** | **0.22** | **1.00** |

Source: `node benchmarks/selftest.js` (output committed below).

```
refund     baseline=0.00  greybeard=1.00  OK
webhook    baseline=0.33  greybeard=1.00  OK
external   baseline=0.00  greybeard=1.00  OK
inventory  baseline=0.00  greybeard=1.00  OK
export     baseline=0.33  greybeard=1.00  OK
logging    baseline=0.67  greybeard=1.00  OK
ALL SELF-TESTS PASSED — grader is valid.
```

## Live model run (to populate)

After running `npx promptfoo eval`, paste the median-of-`repeat` safety scores
per model here, e.g.:

| Model | baseline mean | ponytail mean | greybeard mean |
|-------|--------------:|--------------:|---------------:|
| Haiku  | _tbd_ | _tbd_ | _tbd_ |
| Sonnet | _tbd_ | _tbd_ | _tbd_ |
| Opus   | _tbd_ | _tbd_ | _tbd_ |

## greybeard real multi-model benchmark

_Each cell = mean fraction of production-bug checks passed, averaged across the 7 tasks (each task repeated per model/arm). Graded by the deterministic graders.js — the same contract as the self-test._

| Model | baseline | ponytail | greybeard | greybeard − baseline |
|-------|---------:|---------:|----------:|---------------------:|
| haiku | 0.61 | 0.61 | 0.97 | +0.36 |
| opus | 0.91 | 0.83 | 0.98 | +0.07 |
| sonnet | 0.73 | 0.67 | 1.00 | +0.27 |
| **all** | **0.75** | **0.70** | **0.98** | **+0.23** |

### Per-task (averaged over models)

| Task | baseline | ponytail | greybeard |
|------|---------:|---------:|----------:|
| export | 0.67 | 0.67 | 0.96 |
| external | 0.41 | 0.22 | 1.00 |
| inventory | 0.67 | 0.61 | 1.00 |
| logging | 0.96 | 0.96 | 1.00 |
| outbound | 0.74 | 0.78 | 0.93 |
| refund | 0.81 | 0.67 | 1.00 |
| webhook | 1.00 | 1.00 | 1.00 |

### Full grid (model · arm · task → mean score, n samples)


**haiku**

| Task | baseline | ponytail | greybeard |
|------|----------|----------|-----------|
| export | 0.67 (n=3) | 0.67 (n=3) | 1.00 (n=3) |
| external | 0.00 (n=3) | 0.00 (n=3) | 1.00 (n=3) |
| inventory | 0.50 (n=3) | 0.33 (n=3) | 1.00 (n=3) |
| logging | 0.89 (n=3) | 0.89 (n=3) | 1.00 (n=3) |
| outbound | 0.56 (n=3) | 0.72 (n=3) | 0.78 (n=3) |
| refund | 0.67 (n=3) | 0.67 (n=3) | 1.00 (n=3) |
| webhook | 1.00 (n=3) | 1.00 (n=3) | 1.00 (n=3) |

**opus**

| Task | baseline | ponytail | greybeard |
|------|----------|----------|-----------|
| export | 0.67 (n=3) | 0.67 (n=3) | 0.89 (n=3) |
| external | 0.78 (n=3) | 0.56 (n=3) | 1.00 (n=3) |
| inventory | 1.00 (n=3) | 1.00 (n=3) | 1.00 (n=3) |
| logging | 1.00 (n=3) | 1.00 (n=3) | 1.00 (n=3) |
| outbound | 0.94 (n=3) | 0.89 (n=3) | 1.00 (n=3) |
| refund | 1.00 (n=3) | 0.67 (n=3) | 1.00 (n=3) |
| webhook | 1.00 (n=3) | 1.00 (n=3) | 1.00 (n=3) |

**sonnet**

| Task | baseline | ponytail | greybeard |
|------|----------|----------|-----------|
| export | 0.67 (n=3) | 0.67 (n=3) | 1.00 (n=3) |
| external | 0.44 (n=3) | 0.11 (n=3) | 1.00 (n=3) |
| inventory | 0.50 (n=3) | 0.50 (n=3) | 1.00 (n=3) |
| logging | 1.00 (n=3) | 1.00 (n=3) | 1.00 (n=3) |
| outbound | 0.72 (n=3) | 0.72 (n=3) | 1.00 (n=3) |
| refund | 0.78 (n=3) | 0.67 (n=3) | 1.00 (n=3) |
| webhook | 1.00 (n=3) | 1.00 (n=3) | 1.00 (n=3) |


[wrote benchmarks/results/raw-2026-06-20/_report.scored.json]

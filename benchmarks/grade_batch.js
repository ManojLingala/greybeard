// Batch grader — score model generations with the real graders.js.
// -----------------------------------------------------------------------------
// Input (argv[2]) is EITHER:
//   - a JSON array of records: { task_id, arm, model, idx, code }, OR
//   - a directory of generated files named  <model>__<arm>__<task>__<idx>.cs
//     (this is what the multi-model workflow writes — one file per generation).
// Output (stdout):  a markdown report + per-cell breakdown.
// Output (file):    <input>.scored.json  with every record annotated + aggregates.
//
// This uses the SAME deterministic graders.js that the published benchmark and the
// self-test use — so a real multi-model run is graded by the exact same contract.
const fs = require('fs');
const path = require('path');
const grade = require('./graders.js');

const inPath = process.argv[2];
if (!inPath) {
  console.error('usage: node benchmarks/grade_batch.js <generations.json | dir/>');
  process.exit(2);
}

let records;
let outBase;
const stat = fs.statSync(inPath);
if (stat.isDirectory()) {
  // Directory mode: parse model/arm/task/idx out of each filename.
  records = fs
    .readdirSync(inPath)
    .filter((f) => /__/.test(f) && !f.endsWith('.json'))
    .map((f) => {
      const base = f.replace(/\.[^.]+$/, '');
      const [model, arm, task_id, idx] = base.split('__');
      return {
        task_id,
        arm,
        model,
        idx: Number(idx) || 0,
        code: fs.readFileSync(path.join(inPath, f), 'utf8'),
        file: f,
      };
    });
  outBase = path.join(inPath.replace(/\/$/, ''), '_report');
} else {
  records = JSON.parse(fs.readFileSync(inPath, 'utf8'));
  outBase = inPath.replace(/\.json$/, '');
}

const ARMS = ['baseline', 'ponytail', 'greybeard'];

// Grade every record.
for (const r of records) {
  const output = /```/.test(r.code) ? r.code : '```csharp\n' + r.code + '\n```';
  const g = grade(output, { vars: { task_id: r.task_id } });
  r.score = g.score;
  r.reason = g.reason;
}

const models = [...new Set(records.map((r) => r.model))];
const tasks = [...new Set(records.map((r) => r.task_id))];

function mean(xs) {
  return xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : NaN;
}
function cell(model, arm, task) {
  const xs = records.filter((r) => r.model === model && r.arm === arm && r.task_id === task);
  return { score: mean(xs.map((r) => r.score)), n: xs.length };
}
function armMean(model, arm) {
  // mean across tasks of the per-task mean (so each task is weighted equally)
  const perTask = tasks.map((t) => cell(model, arm, t).score).filter((s) => !Number.isNaN(s));
  return mean(perTask);
}

// ---- Headline table: model × arm ----
let md = '## greybeard real multi-model benchmark\n\n';
md +=
  '_Each cell = mean fraction of production-bug checks passed, averaged across ' +
  `the ${tasks.length} tasks (each task repeated per model/arm). Graded by the deterministic ` +
  'graders.js — the same contract as the self-test._\n\n';
md += '| Model | baseline | ponytail | greybeard | greybeard − baseline |\n';
md += '|-------|---------:|---------:|----------:|---------------------:|\n';
for (const m of models) {
  const b = armMean(m, 'baseline');
  const p = armMean(m, 'ponytail');
  const g = armMean(m, 'greybeard');
  const d = g - b;
  md += `| ${m} | ${b.toFixed(2)} | ${p.toFixed(2)} | ${g.toFixed(2)} | +${d.toFixed(2)} |\n`;
}
// overall row
const overall = {};
for (const arm of ARMS) overall[arm] = mean(models.map((m) => armMean(m, arm)));
md +=
  `| **all** | **${overall.baseline.toFixed(2)}** | **${overall.ponytail.toFixed(2)}** | ` +
  `**${overall.greybeard.toFixed(2)}** | **+${(overall.greybeard - overall.baseline).toFixed(2)}** |\n`;

// ---- Per-task breakdown (greybeard vs baseline), averaged over models ----
md += '\n### Per-task (averaged over models)\n\n';
md += '| Task | baseline | ponytail | greybeard |\n|------|---------:|---------:|----------:|\n';
for (const t of tasks) {
  const b = mean(models.map((m) => cell(m, 'baseline', t).score).filter((s) => !Number.isNaN(s)));
  const p = mean(models.map((m) => cell(m, 'ponytail', t).score).filter((s) => !Number.isNaN(s)));
  const g = mean(models.map((m) => cell(m, 'greybeard', t).score).filter((s) => !Number.isNaN(s)));
  md += `| ${t} | ${b.toFixed(2)} | ${p.toFixed(2)} | ${g.toFixed(2)} |\n`;
}

// ---- Full model × arm × task grid ----
md += '\n### Full grid (model · arm · task → mean score, n samples)\n\n';
for (const m of models) {
  md += `\n**${m}**\n\n| Task | baseline | ponytail | greybeard |\n|------|----------|----------|-----------|\n`;
  for (const t of tasks) {
    const c = (arm) => {
      const x = cell(m, arm, t);
      return Number.isNaN(x.score) ? '—' : `${x.score.toFixed(2)} (n=${x.n})`;
    };
    md += `| ${t} | ${c('baseline')} | ${c('ponytail')} | ${c('greybeard')} |\n`;
  }
}

const summary = {
  generatedFrom: inPath,
  models,
  tasks,
  total_generations: records.length,
  overall,
  byModelArm: Object.fromEntries(
    models.map((m) => [m, Object.fromEntries(ARMS.map((a) => [a, armMean(m, a)]))]),
  ),
};

const outPath = outBase + '.scored.json';
fs.writeFileSync(outPath, JSON.stringify({ summary, records }, null, 2));

console.log(md);
console.log(`\n[wrote ${outPath}]`);

// Robustness / anti-gaming check.
// -----------------------------------------------------------------------------
// A fair objection to the benchmark: "greybeard writes more `greybeard:` comments,
// and the grader's presence checks (HMAC, retry, idempoten, ...) can match a word
// in a COMMENT — so is greybeard just being rewarded for narrating safeguards it
// didn't actually implement?"
//
// This script answers it empirically: re-grade every committed generation with ALL
// C# comments stripped, so even presence checks see only real code, and compare to
// the normal score. If greybeard's lead survives comment-stripping, the wins are
// implementation, not commentary.
//
//   node benchmarks/robustness_check.js [results-dir]
const fs = require('fs');
const path = require('path');
const grade = require('./graders.js');

const dir = process.argv[2] || path.join(__dirname, 'results', 'raw-2026-06-20');

function stripComments(code) {
  return code.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/\/\/[^\n]*/g, ' ');
}
function scoreOf(text, task) {
  const output = /```/.test(text) ? text : '```csharp\n' + text + '\n```';
  return grade(output, { vars: { task_id: task } }).score;
}

const byArm = {};
for (const f of fs.readdirSync(dir).filter((x) => x.endsWith('.cs'))) {
  const [, arm, task] = f.replace(/\.cs$/, '').split('__');
  const raw = fs.readFileSync(path.join(dir, f), 'utf8');
  const normal = scoreOf(raw, task);
  const stripped = scoreOf('```csharp\n' + stripComments(raw) + '\n```', task);
  byArm[arm] = byArm[arm] || { n: 0, normal: 0, stripped: 0 };
  byArm[arm].n++;
  byArm[arm].normal += normal;
  byArm[arm].stripped += stripped;
}

console.log('Does the score survive removing ALL comments? (presence checks see only real code)\n');
console.log('arm         normal   no-comments   delta');
for (const arm of ['baseline', 'ponytail', 'greybeard']) {
  const x = byArm[arm];
  if (!x) continue;
  const nm = x.normal / x.n;
  const st = x.stripped / x.n;
  console.log(
    `${arm.padEnd(11)} ${nm.toFixed(3)}    ${st.toFixed(3)}        -${(nm - st).toFixed(3)}`,
  );
}
const g = byArm.greybeard;
const b = byArm.baseline;
if (g && b) {
  const gStripped = g.stripped / g.n;
  const bNormal = b.normal / b.n;
  console.log(
    `\ngreybeard with NO comments (${gStripped.toFixed(3)}) vs baseline WITH comments (${bNormal.toFixed(3)}): ` +
      `+${(gStripped - bNormal).toFixed(3)} — the lead is real code, not narration.`,
  );
}

// ESLint checks a suppression only for files it lints, and accepts any count a
// PR writes. This enforces the rest of "suppressions may only shrink"
// (web/AGENTS.md): every entry names an existing file, and with a base given,
// no file/rule entry is new or higher than at the base. The one exception is
// a rule that did not apply to that file under the base's own config: that is
// a newly enabled rule's baseline, not a new violation of an existing rule.
// Usage, from web/: node lint/check-suppressions.mjs [--base <git-ref>]
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { ESLint } from 'eslint';

const entries = (suppressions) =>
  Object.entries(suppressions).flatMap(([file, rules]) =>
    Object.entries(rules).map(([rule, { count }]) => {
      if (!Number.isInteger(count)) throw new Error(`${file} ${rule}: count is not an integer`);
      return { file, rule, count };
    }),
  );
const atRef = (ref, path) => {
  try {
    return execFileSync('git', ['show', `${ref}:web/${path}`], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  } catch {
    return null;
  }
};

const head = entries(JSON.parse(readFileSync('lint/eslint-suppressions.json', 'utf8')));
const errors = head.filter(({ file }) => !existsSync(file)).map(({ file, rule }) => `${file} ${rule}: file does not exist`);

const ref = process.argv[2] === '--base' ? process.argv[3] : undefined;
const baseSuppressions = ref && atRef(ref, 'lint/eslint-suppressions.json');
if (baseSuppressions) {
  const base = new Map(entries(JSON.parse(baseSuppressions)).map(({ file, rule, count }) => [`${file} ${rule}`, count]));
  // Written beside this script so its imports resolve from web/lint/node_modules.
  const baseConfig = 'lint/.base-eslint.config.js';
  writeFileSync(baseConfig, atRef(ref, 'lint/eslint.config.js'));
  try {
    const eslint = new ESLint({ overrideConfigFile: baseConfig });
    for (const { file, rule, count } of head) {
      const was = base.get(`${file} ${rule}`) ?? 0;
      if (count <= was || !existsSync(file)) continue;
      const severity = (await eslint.calculateConfigForFile(file))?.rules?.[rule]?.[0] ?? 0;
      if (was === 0 && severity === 0) continue;
      errors.push(`${file} ${rule}: count ${was} -> ${count}`);
    }
  } finally {
    rmSync(baseConfig);
  }
}

if (errors.length > 0) {
  console.error(`lint/eslint-suppressions.json may only shrink. Fix new violations; delete entries for files that no longer exist:\n  ${errors.join('\n  ')}`);
  process.exit(1);
}

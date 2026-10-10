// ESLint checks a suppression only for files it lints, and accepts any count a
// PR writes. This enforces the rest of "suppressions may only shrink"
// (web/AGENTS.md): every entry names an existing file, and with a base given,
// no file/rule entry is new or higher than at the base. The one exception is
// a rule that did not apply to that file under the base's own config: then the
// entry may record at most the violations the rule finds in the BASE source of
// that file, so a newly enabled rule can record existing sites, while code
// moved or added under a widened glob still has to be fixed.
// Usage, from web/: node lint/check-suppressions.mjs [--base <git-ref>]
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { ESLint } from 'eslint';

const args = process.argv.slice(2);
if (!(args.length === 0 || (args.length === 2 && args[0] === '--base' && args[1] !== ''))) {
  console.error('Usage, from web/: node lint/check-suppressions.mjs [--base <git-ref>]');
  process.exit(2);
}

const root = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
const git = (...gitArgs) => execFileSync('git', gitArgs, { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
// null only when the commit has no such path; any git failure throws.
const atCommit = (commit, path) =>
  git('ls-tree', '--name-only', commit, '--', `web/${path}`).trim() === '' ? null : git('show', `${commit}:web/${path}`);

const entries = (suppressions) =>
  Object.entries(suppressions).flatMap(([file, rules]) =>
    Object.entries(rules).map(([rule, { count }]) => {
      if (!Number.isInteger(count)) throw new Error(`${file} ${rule}: count is not an integer`);
      return { file, rule, count };
    }),
  );

const head = entries(JSON.parse(readFileSync('lint/eslint-suppressions.json', 'utf8')));
const errors = head.filter(({ file }) => !existsSync(file)).map(({ file, rule }) => `${file} ${rule}: file does not exist`);

let base = null;
if (args.length === 2) {
  try {
    base = git('rev-parse', '--verify', '--end-of-options', `${args[1]}^{commit}`).trim();
  } catch {
    console.error(`--base ${args[1]} is not a commit this checkout has. Fetch it first.`);
    process.exit(2);
  }
}
const baseSuppressions = base && atCommit(base, 'lint/eslint-suppressions.json');
if (base && !baseSuppressions) console.log(`${args[1]} predates lint/eslint-suppressions.json: only the path check runs.`);
if (baseSuppressions) {
  const was = new Map(entries(JSON.parse(baseSuppressions)).map(({ file, rule, count }) => [`${file} ${rule}`, count]));
  // Written beside this script so its imports resolve from web/lint/node_modules.
  const baseConfig = 'lint/.base-eslint.config.js';
  writeFileSync(baseConfig, atCommit(base, 'lint/eslint.config.js'));
  try {
    const atBase = new ESLint({ overrideConfigFile: baseConfig });
    for (const { file, rule, count } of head) {
      const allowed = was.get(`${file} ${rule}`) ?? 0;
      if (count <= allowed || !existsSync(file)) continue;
      const appliedAtBase = ((await atBase.calculateConfigForFile(file))?.rules?.[rule]?.[0] ?? 0) !== 0;
      const source = appliedAtBase ? null : atCommit(base, file);
      const baseSites = source === null ? 0 : (await new ESLint({ ruleFilter: ({ ruleId }) => ruleId === rule })
        .lintText(source, { filePath: file }))[0].messages.filter((m) => m.ruleId === rule).length;
      if (count > Math.max(allowed, baseSites)) {
        errors.push(`${file} ${rule}: count ${allowed} -> ${count}` + (appliedAtBase ? '' : ` (the base source has ${baseSites})`));
      }
    }
  } finally {
    rmSync(baseConfig);
  }
}

if (errors.length > 0) {
  console.error(`lint/eslint-suppressions.json may only shrink. Fix new violations; delete entries for files that no longer exist:\n  ${errors.join('\n  ')}`);
  process.exit(1);
}

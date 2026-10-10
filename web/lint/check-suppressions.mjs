// ESLint checks a suppression only for files it lints, and accepts any count a
// PR writes. This enforces the rest of "suppressions may only shrink"
// (web/AGENTS.md): every entry names an existing file, and with a base file
// given, no file/rule entry is new or higher than at the base.
// Usage, from web/: node lint/check-suppressions.mjs [base-suppressions.json]
import { existsSync, readFileSync } from 'node:fs';

const read = (path) => JSON.parse(readFileSync(path, 'utf8'));
const entries = (suppressions) =>
  Object.entries(suppressions).flatMap(([file, rules]) =>
    Object.entries(rules).map(([rule, { count }]) => {
      if (!Number.isInteger(count)) throw new Error(`${file} ${rule}: count is not an integer`);
      return { file, rule, count };
    }),
  );

const head = entries(read('lint/eslint-suppressions.json'));
const errors = head.filter(({ file }) => !existsSync(file)).map(({ file, rule }) => `${file} ${rule}: file does not exist`);

const basePath = process.argv[2];
if (basePath) {
  const base = new Map(entries(read(basePath)).map(({ file, rule, count }) => [`${file} ${rule}`, count]));
  for (const { file, rule, count } of head) {
    const was = base.get(`${file} ${rule}`) ?? 0;
    if (count > was) errors.push(`${file} ${rule}: count ${was} -> ${count}`);
  }
}

if (errors.length > 0) {
  console.error(`lint/eslint-suppressions.json may only shrink. Fix new violations; delete entries for files that no longer exist:\n  ${errors.join('\n  ')}`);
  process.exit(1);
}

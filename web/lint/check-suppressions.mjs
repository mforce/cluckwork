// ESLint checks a suppression only for files it lints, so an entry for a
// deleted file stays green and can hide a new violation at a reused path.
// Usage, from web/: node lint/check-suppressions.mjs
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

if (errors.length > 0) {
  console.error(`lint/eslint-suppressions.json may only shrink. Delete entries for files that no longer exist:\n  ${errors.join('\n  ')}`);
  process.exit(1);
}

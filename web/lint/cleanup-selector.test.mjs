// Cases for the #1395 cleanup selector in eslint.config.js, linted in memory with the real config.
// Run from web/: node --test lint/cleanup-selector.test.mjs (CI's Lint step runs it).
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { ESLint } from 'eslint';

const web = fileURLToPath(new URL('..', import.meta.url));
const eslint = new ESLint({ cwd: web });
const cases = {
  block: [1, 'useEffect(() => { return () => { x.current = false; }; }, []);'],
  concise: [1, 'useEffect(() => () => { x.current = false; }, []);'],
  conditional: [1, 'useEffect(() => { if (x.current) return () => { x.current = false; }; }, []);'],
  conditionalBlock: [1, 'useEffect(() => { if (x.current) { return () => { x.current = false; }; } }, []);'],
  functionExpression: [1, 'useEffect(function () { return function () { x.current = false; }; }, []);'],
  member: [1, 'React.useEffect(() => { return () => { x.current = false; }; }, []);'],
  timeout: [0, 'useEffect(() => { setTimeout(() => { x.current = false; }, 10); }, []);'],
  layout: [0, 'useLayoutEffect(() => { return () => { x.current = false; }; }, []);'],
  nestedFactory: [0, 'useEffect(() => { const reset = () => { return () => { x.current = false; }; }; reset()(); }, []);'],
  immediateFactory: [0, 'useEffect(() => { (() => { return () => { x.current = false; }; })()(); }, []);'],
};

for (const [name, [expected, effect]] of Object.entries(cases)) {
  test(`${name}: ${expected} cleanup diagnostic(s)`, async () => {
    const code = 'import React, { useEffect, useLayoutEffect, useRef } from "react";\n'
      + `export function useProbe() { const x = useRef(true); ${effect} }\n`;
    // An existing file path, so the typed parser's project service accepts the in-memory text.
    const [result] = await eslint.lintText(code, { filePath: `${web}src/components/useDialogSession.ts` });
    const found = result.messages.filter((m) => m.ruleId === 'no-restricted-syntax');
    assert.equal(found.length, expected, JSON.stringify(result.messages));
  });
}

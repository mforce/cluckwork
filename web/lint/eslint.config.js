import js from '@eslint/js';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

const inEffect = 'CallExpression:matches([callee.name="useEffect"], [callee.property.name="useEffect"])';
const rawAsyncEffect = 'Load data with useLatestLoad or usePagedList (web/src/components/). '
  + 'A raw async effect lets a superseded response overwrite newer state (#467, #703, #918).';
// The function an effect returns: `return () => …` anywhere in its body, or the body of `() => () => …`.
const clearsFlag = 'AssignmentExpression[left.property.name="current"][right.value=false]';
const mountedFlagCleanup = [
  `${inEffect} > :function > BlockStatement ReturnStatement > :function ${clearsFlag}`,
  `${inEffect} > ArrowFunctionExpression > :function ${clearsFlag}`,
].map((selector) => ({
  selector,
  message: 'Clear a flag that async continuations read in a useLayoutEffect cleanup. A useEffect cleanup can '
    + 'run a task after the commit that removes the screen, so a continuation in between still acts (#1395).',
}));

export default tseslint.config(
  { ignores: ['dist/', 'dev-dist/', 'coverage/', 'lint/'] },
  {
    linterOptions: { reportUnusedDisableDirectives: 'error' },
    files: ['src/**/*.{ts,tsx}'],
    extends: [js.configs.recommended, tseslint.configs.recommended],
    languageOptions: {
      globals: globals.browser,
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname + '/..' },
    },
    plugins: { 'react-hooks': reactHooks },
    rules: {
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
      '@typescript-eslint/no-floating-promises': 'error',
      // tsc's noUnusedLocals/noUnusedParameters already own this.
      '@typescript-eslint/no-unused-vars': 'off',
      // Style, not correctness.
      'prefer-const': 'off',
      'preserve-caught-error': 'off',
      'no-restricted-syntax': ['error', ...mountedFlagCleanup],
    },
  },
  {
    files: ['src/routes/**/*.{ts,tsx}', 'src/components/**/*.{ts,tsx}'],
    ignores: ['**/*.test.{ts,tsx}'],
    rules: {
      'no-restricted-syntax': ['error',
        { selector: `${inEffect} AwaitExpression`, message: rawAsyncEffect },
        { selector: `${inEffect} CallExpression[callee.property.name="then"]`, message: rawAsyncEffect },
        // Repeated: this block's no-restricted-syntax replaces the one above for these files.
        ...mountedFlagCleanup,
      ],
    },
  },
);

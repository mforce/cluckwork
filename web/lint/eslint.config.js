import js from '@eslint/js';
import reactHooks from 'eslint-plugin-react-hooks';
import globals from 'globals';
import tseslint from 'typescript-eslint';

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
    },
  },
);

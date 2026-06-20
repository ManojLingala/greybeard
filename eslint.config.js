// ESLint 9 flat config. Tuned to match the existing benchmark code:
// CommonJS, single quotes, semicolons, 2-space indent.
const js = require('@eslint/js');

module.exports = [
  // Workflow DSL scripts are run by the Claude Code Workflow engine, not Node —
  // they use module syntax + injected globals (agent/parallel/log). Not lint surface.
  { ignores: ['benchmarks/**/*.workflow.js'] },
  js.configs.recommended,
  {
    files: ['benchmarks/**/*.js'],
    ignores: ['benchmarks/**/*.workflow.js'],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'commonjs',
      globals: {
        require: 'readonly',
        module: 'writable',
        process: 'readonly',
        console: 'readonly',
        __dirname: 'readonly',
      },
    },
    rules: {
      'no-unused-vars': ['warn', { argsIgnorePattern: '^_' }],
      'no-console': 'off',
      eqeqeq: ['error', 'smart'],
      'prefer-const': 'warn',
    },
  },
];

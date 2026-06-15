// ESLint 9 flat config. Tuned to match the existing benchmark code:
// CommonJS, single quotes, semicolons, 2-space indent.
const js = require('@eslint/js');

module.exports = [
  js.configs.recommended,
  {
    files: ['benchmarks/**/*.js'],
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

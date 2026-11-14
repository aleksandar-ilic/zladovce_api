/** @type {import('jest').Config} */
module.exports = {
  testEnvironment: 'node',
  roots: ['<rootDir>/test'],
  testMatch: ['**/*.test.ts'],
  transform: {
    '^.+\.ts$': ['ts-jest', { diagnostics: true }],
  },
  // Synthesising all stacks for three environments (plus cdk-nag) is slow on small CI runners.
  testTimeout: 120000,
  // CDK prints validation reports and placeholder-artifact warnings to the console on every synth.
  silent: true,
  // The CDK validation plugins keep a handle open after the last test.
  forceExit: true,
};

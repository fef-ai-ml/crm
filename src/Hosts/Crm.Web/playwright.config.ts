import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  // J19: /v1/auth/login and /v1/auth/refresh share a 20/min rate limit — a single worker
  // and one login per spec file (test.describe.configure({ mode: 'serial' })) stay under it.
  workers: 1,
  use: { baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:3000', trace: 'retain-on-failure' },
  reporter: [['list'], ['html', { open: 'never' }]],
});

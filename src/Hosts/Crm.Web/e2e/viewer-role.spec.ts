import { expect, test, type Page } from '@playwright/test';
import { login } from './helpers';

// docs/dev-setup.md "Logging in" — the seeded Viewer from `make seed`.
const VIEWER_EMAIL = 'viewer@demo.local';
const VIEWER_PASSWORD = 'Demo-Passw0rd!';

// J19: one login per spec file, shared page, serial execution.
test.describe.configure({ mode: 'serial' });

let page: Page;

test.beforeAll(async ({ browser }) => {
  page = await browser.newPage();
  await login(page, VIEWER_EMAIL, VIEWER_PASSWORD);
});

test.afterAll(async () => {
  await page.close();
});

test('E2E-008 — Viewer sees a read-only UI', async () => {
  await page.goto('/segments');
  await expect(page.getByTestId('segment-row').first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'New segment' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'API keys' })).toHaveCount(0);

  await page.getByTestId('segment-row').first().getByRole('link').click();
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

  await expect(page.getByRole('button', { name: 'Save' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Run now' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Archive' })).toHaveCount(0);

  // Definition is the default tab; its inputs render read-only for a Viewer (J9).
  await expect(page.getByLabel('Name', { exact: true })).toBeDisabled();
  await expect(page.getByLabel('Description')).toBeDisabled();
  await expect(page.getByLabel('Status', { exact: true })).toBeDisabled();

  await page.goto('/api-keys');
  await expect(page).toHaveURL(/\/segments$/);
});

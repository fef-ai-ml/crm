import { expect, test, type Page } from '@playwright/test';
import { login } from './helpers';

// docs/dev-setup.md "Logging in" — the seeded Admin from `make seed`.
const ADMIN_EMAIL = 'admin@demo.local';
const ADMIN_PASSWORD = 'Demo-Passw0rd!';

// J19: one login per spec file, shared page, serial execution.
test.describe.configure({ mode: 'serial' });

let page: Page;

test.beforeAll(async ({ browser }) => {
  page = await browser.newPage();
  await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
});

test.afterAll(async () => {
  await page.close();
});

test('E2E-007 — API key lifecycle in the UI', async ({ request }) => {
  const name = `sdk-e2e-${Date.now()}`;

  await page.goto('/api-keys');
  await page.getByLabel('Key name').fill(name);
  await page.getByRole('button', { name: 'Create key' }).click();

  const secret = await page.getByTestId('api-key-secret').innerText();
  expect(secret.length).toBeGreaterThan(8);

  await page.getByRole('button', { name: 'Close' }).click();
  await expect(page.getByTestId('api-key-secret')).toHaveCount(0);

  const row = page.getByTestId('api-key-row').filter({ hasText: name });
  await expect(row).toBeVisible();
  const prefix = await row.locator('td').nth(1).innerText();
  expect(prefix).toHaveLength(8);

  await row.getByRole('button', { name: 'Revoke' }).click();
  await row.getByRole('button', { name: 'Confirm revoke' }).click();

  await expect(row.getByRole('button', { name: /Revoke/ })).toHaveCount(0);
  await expect(row.locator('td').nth(4)).not.toHaveText('—');

  const trackResponse = await request.post('/v1/track', {
    headers: { 'X-Api-Key': secret, 'Content-Type': 'application/json' },
    data: { userId: 'e2e-u1', event: 'purchase' },
  });
  expect(trackResponse.status()).toBe(401);
});

import { expect, test } from '@playwright/test';
import { login } from './helpers';

// docs/dev-setup.md "Logging in" — the seeded Admin from `make seed`.
const ADMIN_EMAIL = 'admin@demo.local';
const ADMIN_PASSWORD = 'Demo-Passw0rd!';

// E2E-001 (docs/test/e2e-load.md) — J19: one spec file, at most a couple of logins.
test.describe.configure({ mode: 'serial' });

test('login reaches the dashboard with the access token in memory only (E2E-001)', async ({ page, context }) => {
  await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);

  await expect(page).toHaveURL(/\/segments$/);

  const localStorageKeys = await page.evaluate(() => Object.keys(localStorage));
  const sessionStorageKeys = await page.evaluate(() => Object.keys(sessionStorage));
  expect(localStorageKeys).toEqual([]);
  expect(sessionStorageKeys).toEqual([]);

  const cookies = await context.cookies();
  const refreshCookie = cookies.find((cookie) => cookie.name === 'crm_refresh');
  expect(refreshCookie).toBeDefined();
  expect(refreshCookie?.httpOnly).toBe(true);
  expect(refreshCookie?.path).toBe('/v1/auth');
});

test('a wrong password shows the problem+json detail', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Email').fill(ADMIN_EMAIL);
  await page.getByLabel('Password').fill('definitely-wrong-password');
  await page.getByRole('button', { name: /Sign in/ }).click();

  await expect(page.getByRole('alert')).toHaveText('Email or password is incorrect.');
});

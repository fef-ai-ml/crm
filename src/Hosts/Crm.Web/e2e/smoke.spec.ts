import { expect, test } from '@playwright/test';

test('status page shows all readiness checks healthy', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: /Platform status: Healthy/ })).toBeVisible();
  for (const name of ['sqlserver', 'redis', 'kafka']) {
    await expect(page.getByTestId(`check-${name}`)).toHaveText(`${name}: Healthy`);
  }
});

test('login page renders the disabled form', async ({ page }) => {
  await page.goto('/login');
  await expect(page.getByRole('button', { name: /Sign in/ })).toBeDisabled();
});

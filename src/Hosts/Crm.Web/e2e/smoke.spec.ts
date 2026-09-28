import { expect, test } from '@playwright/test';
import { login } from './helpers';

test('status page shows all readiness checks healthy', async ({ page }) => {
  await login(page, 'admin@demo.local', 'Demo-Passw0rd!');
  await page.goto('/status');
  await expect(page.getByRole('heading', { name: /Platform status: Healthy/ })).toBeVisible();
  for (const name of ['sqlserver', 'redis', 'kafka']) {
    await expect(page.getByTestId(`check-${name}`)).toHaveText(`${name}: Healthy`);
  }
});

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

/** The default filter is "Draft + Active" (no `status` param, H14), which already includes every seed row this
 * test needs; a seed segment might still land on a later page, so this pages through with "Load more" until
 * the row is visible or there is nothing left to load. */
async function ensureRowVisible(target: Page, name: string): Promise<void> {
  for (let i = 0; i < 10; i++) {
    if ((await target.getByText(name).count()) > 0) return;
    const loadMore = target.getByRole('button', { name: 'Load more' });
    if ((await loadMore.count()) === 0) return;
    await loadMore.click();
  }
}

test('E2E-005 — segment list columns and badges', async () => {
  await page.goto('/segments');

  await ensureRowVisible(page, 'seed-pro-id');
  const proIdRow = page.getByTestId('segment-row').filter({ hasText: 'seed-pro-id' });
  await expect(proIdRow.getByTestId('segment-badge')).toHaveText('batch');
  await expect(proIdRow.getByText('Active')).toBeVisible();
  await expect(proIdRow).toContainText('412');
  await expect(proIdRow).not.toContainText('—'); // Active segments have a non-empty "Last run".

  await ensureRowVisible(page, 'seed-purchasers');
  const purchasersRow = page.getByTestId('segment-row').filter({ hasText: 'seed-purchasers' });
  await expect(purchasersRow.getByTestId('segment-badge')).toHaveText('real-time');
  await expect(purchasersRow.getByText('Active')).toBeVisible();
  await expect(purchasersRow).not.toContainText('—');

  await ensureRowVisible(page, 'seed-high-ltv-sg');
  const draftRow = page.getByTestId('segment-row').filter({ hasText: 'seed-high-ltv-sg' });
  await expect(draftRow.getByText('Draft')).toBeVisible();
});

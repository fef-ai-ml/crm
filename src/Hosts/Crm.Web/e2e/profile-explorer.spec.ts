import { expect, test, type Locator, type Page } from '@playwright/test';
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

/** Reads the "Events" cell of a `profile-row` locator as a number. */
async function eventCount(row: Locator): Promise<number> {
  const cells = row.locator('td');
  const text = await cells.nth(3).innerText();
  return Number(text.trim());
}

/** Fixtures.md: 5,000 profiles / 100,000 events skewed across names, so most profiles have well under 50
 * events — this scans the visible `alice` results (paging once if needed) for the first one with > 50, the
 * seed row E2E-006 needs to exercise a second timeline page. */
async function findProfileWithManyEvents(target: Page): Promise<Locator | null> {
  for (let i = 0; i < 2; i++) {
    const rows = await target.getByTestId('profile-row').all();
    for (const row of rows) {
      if ((await eventCount(row)) > 50) return row;
    }
    const loadMore = target.getByRole('button', { name: 'Load more' });
    if ((await loadMore.count()) === 0) return null;
    await loadMore.click();
  }
  return null;
}

test('E2E-006 — profile explorer search and timeline', async () => {
  await page.goto('/profiles');

  await page.getByLabel('Search profiles').fill('alice');

  const firstRow = page.getByTestId('profile-row').first();
  await expect(firstRow).toBeVisible();
  await expect(firstRow.getByText(/alice\./)).toBeVisible();

  await firstRow.getByRole('button').click();

  const drawer = page.getByRole('dialog', { name: 'Profile' });
  await expect(drawer).toBeVisible();
  await expect(drawer.getByTestId('attributes-json')).toBeVisible();
  await expect(drawer.getByTestId('timeline-row').first()).toBeVisible();
  await drawer.getByRole('button', { name: 'Close' }).click();
  await expect(drawer).toBeHidden();

  // A second timeline page needs a profile with > 50 events; not every `alice` result has one.
  const bigProfileRow = await findProfileWithManyEvents(page);
  if (!bigProfileRow) {
    test.skip(true, 'No profile among the visible "alice" search results (first page + one Load more) has > 50 events; skipping the second-page assertion.');
    return;
  }

  await bigProfileRow.getByRole('button').click();
  const bigDrawer = page.getByRole('dialog', { name: 'Profile' });
  await expect(bigDrawer).toBeVisible();

  const sentinel = bigDrawer.getByTestId('timeline-sentinel');
  await sentinel.scrollIntoViewIfNeeded();

  try {
    await expect
      .poll(async () => (await bigDrawer.getByTestId('timeline-row').count()) > 50, { timeout: 5000 })
      .toBe(true);
  } catch {
    // Fallback (J10): the sentinel's IntersectionObserver may not fire in a headless viewport reliably —
    // the "Load more" button shares the same guard and is the documented fallback.
    await bigDrawer.getByRole('button', { name: 'Load more' }).click();
    await expect
      .poll(async () => (await bigDrawer.getByTestId('timeline-row').count()) > 50, { timeout: 5000 })
      .toBe(true);
  }
});

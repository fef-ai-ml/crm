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

test('E2E-002 — build a nested segment with live count', async () => {
  await page.goto('/segments/new');

  // The root `and` group already has one attribute leaf (newDefinition()): plan eq pro.
  const root = page.getByTestId('rule-node-root');
  await root.getByLabel('Key').fill('plan');
  await root.getByLabel('Comparator').selectOption('eq');
  await root.getByLabel('Value').fill('pro');

  await root.getByRole('button', { name: 'Add group' }).click();

  // The nested group becomes rule-node-1 (second child of root); set its operator to "or".
  const nestedGroup = page.getByTestId('rule-node-1');
  await nestedGroup.getByLabel('Operator').selectOption('or');

  // Its first (default) leaf, rule-node-1-0: country in [ID, SG].
  const countryLeaf = page.getByTestId('rule-node-1-0');
  await countryLeaf.getByLabel('Key').fill('country');
  await countryLeaf.getByLabel('Comparator').selectOption('in');
  await countryLeaf.getByLabel('Value').fill('ID, SG');

  await nestedGroup.getByRole('button', { name: 'Add event' }).click();

  // The event leaf lands at rule-node-1-1: app_open eq 0 in 7d.
  const eventLeaf = page.getByTestId('rule-node-1-1');
  await eventLeaf.getByLabel('Event name').fill('app_open');
  await eventLeaf.getByLabel('Comparator').selectOption('eq');
  await eventLeaf.getByLabel('Count').fill('0');
  await eventLeaf.getByLabel('Window amount').fill('7');
  await eventLeaf.getByLabel('Window unit').selectOption('d');

  await expect(page.getByTestId('live-count')).toContainText('412', { timeout: 3000 });
  await expect(page.getByTestId('segment-badge')).toHaveText('batch');

  const name = `e2e-${Date.now()}`;
  await page.getByLabel('Name').fill(name);

  await page.getByRole('button', { name: 'Save' }).click();
  await page.waitForURL(/\/segments\/[0-9a-fA-F-]{36}$/);
});

test('E2E-003 — validation errors surface on the field', async () => {
  await page.goto('/segments/new');

  const root = page.getByTestId('rule-node-root');
  await root.getByLabel('Key').fill('plan');
  await root.getByLabel('Value').fill('pro');

  await root.getByRole('button', { name: 'Add event' }).click();

  const eventLeaf = page.getByTestId('rule-node-1');
  await eventLeaf.getByLabel('Event name').fill('purchase');
  await eventLeaf.getByLabel('Window amount').fill('400');
  await eventLeaf.getByLabel('Window unit').selectOption('d');

  const badName = `e2e-003-${Date.now()}`;
  await page.getByLabel('Name').fill(badName);

  // The client validator would ALSO reject a 400-day window, so this proves the message on screen came from
  // the server's `errors` map (J7), not the client walker: the request is asserted to have actually gone out
  // and come back 400 (SegmentForm's Save ruling, E2E-003 — see task-4-report.md). The predicate is anchored
  // to the exact create path so it can't match `POST /v1/segments/preview`, which the live-count debounce
  // also fires as a POST (I-2).
  const [response] = await Promise.all([
    page.waitForResponse((res) => new URL(res.url()).pathname === '/v1/segments' && res.request().method() === 'POST'),
    page.getByRole('button', { name: 'Save' }).click(),
  ]);

  expect(response.status()).toBe(400);
  await expect(eventLeaf.getByText('window must match ^\\d+(d|h)$ and be at most 365 days')).toBeVisible();

  await page.goto('/segments');
  await expect(page.getByText(badName)).toHaveCount(0);
});

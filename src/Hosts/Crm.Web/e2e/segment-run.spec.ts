import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { login } from './helpers';

// docs/dev-setup.md "Logging in" — the seeded Admin from `make seed`.
const ADMIN_EMAIL = 'admin@demo.local';
const ADMIN_PASSWORD = 'Demo-Passw0rd!';

// J19: one login per spec file, shared page, serial execution.
test.describe.configure({ mode: 'serial' });

let page: Page;
let segmentId: string;

// Never `import` a fixture (CT-002): resolve `import.meta.url` to a path first, then join.
function loadProIdDefinition(): unknown {
  const fixturePath = path.join(
    path.dirname(fileURLToPath(import.meta.url)),
    '..',
    '..',
    '..',
    '..',
    'tests',
    'fixtures',
    'rules',
    'valid',
    'seed-pro-id.json',
  );
  const raw = readFileSync(fixturePath, 'utf-8');
  return (JSON.parse(raw) as { definition: unknown }).definition;
}

/** Arranges a fresh Active segment (seed-pro-id's own definition, so its member count is the well-known 412
 * fixture) directly through the API, bypassing the builder UI — the run/members/runs flow under test doesn't
 * depend on how the segment was authored. */
async function createSegment(request: APIRequestContext): Promise<string> {
  const loginResponse = await request.post('/v1/auth/login', {
    data: { email: ADMIN_EMAIL, password: ADMIN_PASSWORD },
  });
  const { accessToken } = (await loginResponse.json()) as { accessToken: string };

  const createResponse = await request.post('/v1/segments', {
    headers: { Authorization: `Bearer ${accessToken}` },
    data: {
      name: `e2e-run-${Date.now()}`,
      definition: loadProIdDefinition(),
      status: 'Active',
    },
  });
  const segment = (await createResponse.json()) as { id: string };
  return segment.id;
}

test.beforeAll(async ({ browser, request }) => {
  segmentId = await createSegment(request);
  page = await browser.newPage();
  await login(page, ADMIN_EMAIL, ADMIN_PASSWORD);
});

test.afterAll(async () => {
  await page.close();
});

test('E2E-004 — run now shows members and runs', async () => {
  await page.goto(`/segments/${segmentId}`);

  await page.getByRole('button', { name: 'Run now' }).click();
  await expect(page.getByRole('status')).toHaveText('Run queued');
  await expect(page.getByRole('tab', { name: 'Runs' })).toHaveAttribute('aria-selected', 'true');

  const firstRun = page.getByTestId('run-row').first();
  await expect(firstRun).toContainText('Finished', { timeout: 10_000 });
  await expect(firstRun).toContainText('412');
  await expect(firstRun).toContainText('Manual');

  await page.getByRole('tab', { name: 'Members' }).click();
  const memberRows = page.getByTestId('member-row');
  await expect(memberRows).toHaveCount(50);
  const firstMemberRow = memberRows.first();
  await expect(firstMemberRow).toContainText('@'); // email column is populated
  await expect(firstMemberRow).not.toContainText('—'); // no placeholder dashes on a real row

  await page.getByRole('button', { name: 'Load more' }).click();
  await expect(memberRows).toHaveCount(100);
});

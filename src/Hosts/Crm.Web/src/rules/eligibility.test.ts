import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { isRealtimeEligible } from './eligibility';
import type { RuleTree } from './types';

// Not `new URL(relative, import.meta.url)`: Vite's import-analysis plugin rewrites that exact literal shape
// into a dev-server URL, not a `file:` URL (see fixtures.test.ts). Resolve the path first, then join.
const RULES_ROOT = path.join(path.dirname(fileURLToPath(import.meta.url)), '../../../../../tests/fixtures/rules');

// Derived from `RealtimeEligibility.cs` over all 26 valid fixtures (J8): no `not` group, every attribute leaf,
// every event leaf with `cmp` `gte` and `count >= 1`.
const ELIGIBLE = [
  'attr-bool',
  'attr-contains',
  'attr-eq',
  'attr-exists',
  'attr-gt',
  'attr-gte',
  'attr-in',
  'attr-lt',
  'attr-lte',
  'attr-neq',
  'attr-nin',
  'attr-not-exists',
  'depth-5',
  'event-gte',
  'leaves-20',
  'seed-high-ltv-sg',
  'seed-no-plan',
  'seed-not-pro',
  'seed-purchasers',
];

const INELIGIBLE = ['event-eq-n', 'event-eq-zero', 'event-lte', 'group-nested', 'group-not', 'seed-dormant', 'seed-pro-id'];

function loadTree(name: string): RuleTree {
  const raw = readFileSync(`${RULES_ROOT}/valid/${name}.json`, 'utf-8');
  return (JSON.parse(raw) as { definition: RuleTree }).definition;
}

describe('isRealtimeEligible (UT-061)', () => {
  it('covers exactly the 26 valid fixtures with no overlap', () => {
    expect(ELIGIBLE.length + INELIGIBLE.length).toBe(26);
    expect(new Set([...ELIGIBLE, ...INELIGIBLE]).size).toBe(26);
  });

  it.each(ELIGIBLE)('%s is real-time eligible', (name) => {
    expect(isRealtimeEligible(loadTree(name))).toBe(true);
  });

  it.each(INELIGIBLE)('%s is not real-time eligible', (name) => {
    expect(isRealtimeEligible(loadTree(name))).toBe(false);
  });
});

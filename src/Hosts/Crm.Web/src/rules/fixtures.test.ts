import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { firstErrorPath, validateRule } from './validate';
import { ruleSchema } from './schema';

// Never `import` a fixture: the web Docker build context is `src/Hosts/Crm.Web` only and does not include
// `tests/`. Read with `node:fs` at test time instead, resolved from `import.meta.url` (CT-002).
//
// Deliberately not `new URL('../../../../../tests/fixtures/rules', import.meta.url)`: Vite's import-analysis
// plugin statically pattern-matches that exact literal `new URL(relative, import.meta.url)` shape (the
// asset-import idiom) and rewrites it to a dev-server URL (`http://localhost:.../@fs/...`) instead of leaving
// it a real `file:` URL, which then makes `fileURLToPath` throw. Resolving `import.meta.url` to a path first,
// then joining, avoids that rewrite.
const RULES_ROOT = path.join(path.dirname(fileURLToPath(import.meta.url)), '../../../../../tests/fixtures/rules');

// Pinned exactly as `tests/Segmentation.UnitTests/Fixtures.cs:10-25` (the C# source of truth for this list).
const VALID = [
  'attr-eq',
  'attr-neq',
  'attr-in',
  'attr-nin',
  'attr-gt',
  'attr-gte',
  'attr-lt',
  'attr-lte',
  'attr-contains',
  'attr-exists',
  'attr-not-exists',
  'attr-bool',
  'event-gte',
  'event-eq-zero',
  'event-eq-n',
  'event-lte',
  'group-nested',
  'group-not',
  'depth-5',
  'leaves-20',
  'seed-pro-id',
  'seed-purchasers',
  'seed-dormant',
  'seed-no-plan',
  'seed-not-pro',
  'seed-high-ltv-sg',
];

const INVALID = [
  'depth-6',
  'leaves-21',
  'not-two-children',
  'not-no-children',
  'window-366d',
  'window-1w',
  'window-0d',
  'window-bare',
  'key-dot',
  'key-quote',
  'key-65',
  'cmp-unknown',
  'event-count-negative',
  'type-unknown',
  'value-infinite',
];

type Fixture = { definition: unknown; expectedErrorPath: string | null };

function loadFixture(kind: 'valid' | 'invalid', name: string): Fixture {
  const filePath = path.join(RULES_ROOT, kind, `${name}.json`);
  const raw = JSON.parse(readFileSync(filePath, 'utf-8')) as { definition: unknown; expectedErrorPath?: string };
  return { definition: raw.definition, expectedErrorPath: raw.expectedErrorPath ?? null };
}

function fileNamesIn(kind: 'valid' | 'invalid'): string[] {
  return readdirSync(path.join(RULES_ROOT, kind))
    .filter((name) => name.endsWith('.json'))
    .map((name) => name.replace(/\.json$/, ''));
}

describe('rule fixture parity, the Zod half of CT-002', () => {
  it('the valid and invalid directories contain exactly the documented files', () => {
    expect(fileNamesIn('valid').sort()).toEqual([...VALID].sort());
    expect(fileNamesIn('invalid').sort()).toEqual([...INVALID].sort());
  });

  it.each(VALID)('valid/%s.json parses with no errors', (name) => {
    const { definition } = loadFixture('valid', name);

    expect(validateRule(definition)).toEqual([]);
    expect(ruleSchema.safeParse(definition).success).toBe(true);
  });

  it.each(INVALID)('invalid/%s.json fails at its expected first error path', (name) => {
    const { definition, expectedErrorPath } = loadFixture('invalid', name);

    expect(firstErrorPath(definition)).toBe(expectedErrorPath);
    expect(ruleSchema.safeParse(definition).success).toBe(false);
  });
});

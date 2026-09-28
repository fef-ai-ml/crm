import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { formatPath, nodeAt, parseErrorPath } from './paths';
import type { RuleTree } from './types';

// Not `new URL(relative, import.meta.url)`: Vite's import-analysis plugin rewrites that exact literal shape
// into a dev-server URL, not a `file:` URL (see fixtures.test.ts). Resolve the path first, then join.
const RULES_ROOT = path.join(path.dirname(fileURLToPath(import.meta.url)), '../../../../../tests/fixtures/rules');

describe('parseErrorPath', () => {
  it('parses the root path with no field', () => {
    expect(parseErrorPath('definition')).toEqual({ indexes: [], field: null });
  });

  it('parses a nested child path with a trailing field', () => {
    expect(parseErrorPath('definition.children[1].key')).toEqual({ indexes: [1], field: 'key' });
  });

  it('returns null for anything that is not a definition-rooted error path', () => {
    expect(parseErrorPath('bogus')).toBeNull();
    expect(parseErrorPath('')).toBeNull();
    expect(parseErrorPath('definition.children[abc].key')).toBeNull();
    expect(parseErrorPath('definitionX')).toBeNull();
  });

  it('round-trips through formatPath', () => {
    const cases: Array<[string, { indexes: number[]; field: string | null }]> = [
      ['definition', { indexes: [], field: null }],
      ['definition.op', { indexes: [], field: 'op' }],
      ['definition.children[0]', { indexes: [0], field: null }],
      ['definition.children[1].key', { indexes: [1], field: 'key' }],
      ['definition.children[0].children[2].window', { indexes: [0, 2], field: 'window' }],
    ];

    for (const [path, expected] of cases) {
      const parsed = parseErrorPath(path);
      expect(parsed).toEqual(expected);
      expect(formatPath(parsed!.indexes, parsed!.field ?? undefined)).toBe(path);
    }
  });
});

describe('nodeAt', () => {
  const tree = (
    JSON.parse(readFileSync(`${RULES_ROOT}/valid/group-nested.json`, 'utf-8')) as { definition: RuleTree }
  ).definition;

  it('returns the root at an empty path', () => {
    expect(nodeAt(tree, [])).toBe(tree);
  });

  it('finds a top-level child by index', () => {
    expect(nodeAt(tree, [0])).toEqual({ type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' });
    expect(nodeAt(tree, [1])).toEqual({
      type: 'event',
      name: 'purchase',
      cmp: 'gte',
      count: 3,
      window: '30d',
    });
  });

  it('finds a grandchild through a nested group', () => {
    expect(nodeAt(tree, [2, 0])).toEqual({
      type: 'attribute',
      key: 'country',
      cmp: 'in',
      value: ['ID', 'SG'],
    });
    expect(nodeAt(tree, [2, 1])).toEqual({
      type: 'event',
      name: 'app_open',
      cmp: 'eq',
      count: 0,
      window: '7d',
    });
  });

  it('returns undefined past an out-of-range index or into a leaf', () => {
    expect(nodeAt(tree, [99])).toBeUndefined();
    expect(nodeAt(tree, [0, 0])).toBeUndefined();
  });
});

import { describe, expect, it } from 'vitest';
import { validateRule } from './validate';
import type { RuleError } from './types';

function leaf(key = 'k'): unknown {
  return { type: 'attribute', key, cmp: 'exists' };
}

function nestAnd(depth: number, inner: unknown): unknown {
  let node = inner;
  for (let i = 0; i < depth; i++) node = { op: 'and', children: [node] };
  return node;
}

type Case = { name: string; tree: unknown; expected: RuleError[] };

const CASES: Case[] = [
  {
    name: 'a non-object node is "unknown node type" at the node path',
    tree: 'not-an-object',
    expected: [{ path: 'definition', message: 'unknown node type' }],
  },
  {
    name: 'an unrecognized group operator is "unknown group operator" at .op',
    tree: { op: 'xor', children: [leaf()] },
    expected: [{ path: 'definition.op', message: 'unknown group operator' }],
  },
  {
    name: 'a not with two children is "not must have exactly one child"',
    tree: { op: 'not', children: [leaf('a'), leaf('b')] },
    expected: [{ path: 'definition', message: 'not must have exactly one child' }],
  },
  {
    name: 'an and/or with no children is "group must have at least one child"',
    tree: { op: 'and', children: [] },
    expected: [{ path: 'definition', message: 'group must have at least one child' }],
  },
  {
    name: 'an unrecognized node type is "unknown node type" at .type',
    tree: { type: 'bogus' },
    expected: [{ path: 'definition.type', message: 'unknown node type' }],
  },
  {
    name: 'a key not matching the key pattern is "key must match ..." at .key',
    tree: { type: 'attribute', key: 'bad key!', cmp: 'eq', value: 'x' },
    expected: [{ path: 'definition.key', message: 'key must match ^[A-Za-z0-9_]{1,64}$' }],
  },
  {
    name: 'an unrecognized attribute comparator is "unknown comparator" at .cmp',
    tree: { type: 'attribute', key: 'ok_key', cmp: 'zzz' },
    expected: [{ path: 'definition.cmp', message: 'unknown comparator' }],
  },
  {
    name: 'exists with a value present is "value is not allowed for this comparator"',
    tree: { type: 'attribute', key: 'tier', cmp: 'exists', value: 'x' },
    expected: [{ path: 'definition.value', message: 'value is not allowed for this comparator' }],
  },
  {
    name: 'not_exists with a value present is "value is not allowed for this comparator"',
    tree: { type: 'attribute', key: 'tier', cmp: 'not_exists', value: null },
    expected: [{ path: 'definition.value', message: 'value is not allowed for this comparator' }],
  },
  {
    name: 'gt with no value is "value is required"',
    tree: { type: 'attribute', key: 'ltv', cmp: 'gt' },
    expected: [{ path: 'definition.value', message: 'value is required' }],
  },
  {
    name: 'gt with a non-number value is "value must be a number"',
    tree: { type: 'attribute', key: 'ltv', cmp: 'gt', value: 'abc' },
    expected: [{ path: 'definition.value', message: 'value must be a number' }],
  },
  {
    name: 'contains with no value is "value is required"',
    tree: { type: 'attribute', key: 'campaign', cmp: 'contains' },
    expected: [{ path: 'definition.value', message: 'value is required' }],
  },
  {
    name: 'contains with a non-string value is "value must be a string"',
    tree: { type: 'attribute', key: 'campaign', cmp: 'contains', value: 5 },
    expected: [{ path: 'definition.value', message: 'value must be a string' }],
  },
  {
    name: 'contains with a value over 256 characters is "value must be at most 256 characters"',
    tree: { type: 'attribute', key: 'campaign', cmp: 'contains', value: 'a'.repeat(257) },
    expected: [{ path: 'definition.value', message: 'value must be at most 256 characters' }],
  },
  {
    name: 'in with no value is "value is required"',
    tree: { type: 'attribute', key: 'country', cmp: 'in' },
    expected: [{ path: 'definition.value', message: 'value is required' }],
  },
  {
    name: 'in with an empty array is "value must be a non-empty array of strings or of numbers (at most 100)"',
    tree: { type: 'attribute', key: 'country', cmp: 'in', value: [] },
    expected: [
      { path: 'definition.value', message: 'value must be a non-empty array of strings or of numbers (at most 100)' },
    ],
  },
  {
    name: 'in with a mixed-type array is "value must be a non-empty array of strings or of numbers (at most 100)"',
    tree: { type: 'attribute', key: 'country', cmp: 'in', value: ['ID', 1] },
    expected: [
      { path: 'definition.value', message: 'value must be a non-empty array of strings or of numbers (at most 100)' },
    ],
  },
  {
    name: 'in with more than 100 items is "value must be a non-empty array of strings or of numbers (at most 100)"',
    tree: { type: 'attribute', key: 'country', cmp: 'in', value: Array.from({ length: 101 }, (_, i) => `v${i}`) },
    expected: [
      { path: 'definition.value', message: 'value must be a non-empty array of strings or of numbers (at most 100)' },
    ],
  },
  {
    name: 'eq with no value is "value is required"',
    tree: { type: 'attribute', key: 'plan', cmp: 'eq' },
    expected: [{ path: 'definition.value', message: 'value is required' }],
  },
  {
    name: 'eq with a value over 256 characters is "value must be at most 256 characters"',
    tree: { type: 'attribute', key: 'plan', cmp: 'eq', value: 'a'.repeat(257) },
    expected: [{ path: 'definition.value', message: 'value must be at most 256 characters' }],
  },
  {
    name: 'eq with an object value is "value must be a string, number or boolean" (H24)',
    tree: { type: 'attribute', key: 'plan', cmp: 'eq', value: { nope: true } },
    expected: [{ path: 'definition.value', message: 'value must be a string, number or boolean' }],
  },
  {
    name: 'an unknown comparator with a value present still applies the scalar rules',
    tree: { type: 'attribute', key: 'plan', cmp: 'zzz', value: { nope: true } },
    expected: [
      { path: 'definition.cmp', message: 'unknown comparator' },
      { path: 'definition.value', message: 'value must be a string, number or boolean' },
    ],
  },
  {
    name: 'an empty event name is "name is required" at .name',
    tree: { type: 'event', name: '', cmp: 'eq', count: 1, window: '30d' },
    expected: [{ path: 'definition.name', message: 'name is required' }],
  },
  {
    name: 'an event name over 128 characters is "name is required" at .name',
    tree: { type: 'event', name: 'a'.repeat(129), cmp: 'eq', count: 1, window: '30d' },
    expected: [{ path: 'definition.name', message: 'name is required' }],
  },
  {
    name: 'an unrecognized event comparator is "unknown comparator" at .cmp',
    tree: { type: 'event', name: 'purchase', cmp: 'zzz', count: 1, window: '30d' },
    expected: [{ path: 'definition.cmp', message: 'unknown comparator' }],
  },
  {
    name: 'a negative count is "count must be at least 0" (H23: 3.0 is a valid count, not tested as an error)',
    tree: { type: 'event', name: 'purchase', cmp: 'gte', count: -1, window: '30d' },
    expected: [{ path: 'definition.count', message: 'count must be at least 0' }],
  },
  {
    name: 'a non-integer count is "count must be at least 0"',
    tree: { type: 'event', name: 'purchase', cmp: 'gte', count: 1.5, window: '30d' },
    expected: [{ path: 'definition.count', message: 'count must be at least 0' }],
  },
  {
    name: 'a malformed window is "window must match ^\\d+(d|h)$ and be at most 365 days"',
    tree: { type: 'event', name: 'purchase', cmp: 'gte', count: 1, window: '30' },
    expected: [{ path: 'definition.window', message: 'window must match ^\\d+(d|h)$ and be at most 365 days' }],
  },
  {
    name: 'a window over the day/hour maximum is "window must match ^\\d+(d|h)$ and be at most 365 days"',
    tree: { type: 'event', name: 'purchase', cmp: 'gte', count: 1, window: '8761h' },
    expected: [{ path: 'definition.window', message: 'window must match ^\\d+(d|h)$ and be at most 365 days' }],
  },
  {
    name: 'depth 6 is "depth must be at most 5" at the root',
    tree: nestAnd(6, leaf()),
    expected: [{ path: 'definition', message: 'depth must be at most 5' }],
  },
  {
    name: '21 leaves is "at most 20 leaves" at the root',
    tree: { op: 'and', children: Array.from({ length: 21 }, (_, i) => leaf(`k${i}`)) },
    expected: [{ path: 'definition', message: 'at most 20 leaves' }],
  },
];

describe('validateRule message catalogue (UT-060)', () => {
  it.each(CASES)('$name', ({ tree, expected }) => {
    expect(validateRule(tree)).toEqual(expected);
  });

  it('a valid tree reports no errors', () => {
    expect(validateRule({ type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' })).toEqual([]);
  });
});

describe('validateRule error order (H19, UT-060)', () => {
  it('a group error is reported before its children errors, in child index order', () => {
    const tree = {
      op: 'not',
      children: [{ type: 'attribute', key: 'bad key', cmp: 'eq', value: 'x' }, leaf('ok_key2')],
    };

    expect(validateRule(tree)).toEqual([
      { path: 'definition', message: 'not must have exactly one child' },
      { path: 'definition.children[0].key', message: 'key must match ^[A-Za-z0-9_]{1,64}$' },
    ]);
  });

  it('depth and leaf-count errors come after every node-level error', () => {
    const badKeyPath = `definition${'.children[0]'.repeat(5)}.children[1].key`;
    const tree = nestAnd(5, {
      op: 'and',
      children: [leaf('a'), { type: 'attribute', key: 'bad key!', cmp: 'eq', value: 'x' }],
    });

    const errors = validateRule(tree);

    expect(errors[0]).toEqual({ path: badKeyPath, message: 'key must match ^[A-Za-z0-9_]{1,64}$' });
    expect(errors[errors.length - 1]).toEqual({ path: 'definition', message: 'depth must be at most 5' });
    expect(errors).toHaveLength(2);
  });

  it('the depth error is reported before the leaf-count error when both fire', () => {
    const manyLeaves = { op: 'and', children: Array.from({ length: 21 }, (_, i) => leaf(`k${i}`)) };
    const tree = nestAnd(5, manyLeaves);

    expect(validateRule(tree)).toEqual([
      { path: 'definition', message: 'depth must be at most 5' },
      { path: 'definition', message: 'at most 20 leaves' },
    ]);
  });
});

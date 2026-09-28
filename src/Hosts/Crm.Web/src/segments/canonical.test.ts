import { describe, expect, it } from 'vitest';
import { canonicalJson } from './canonical';

describe('canonicalJson', () => {
  it('produces the same string for objects that differ only in key order', () => {
    const a = { op: 'and', children: [{ type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' }] };
    const b = { children: [{ value: 'pro', cmp: 'eq', key: 'plan', type: 'attribute' }], op: 'and' };

    expect(canonicalJson(a)).toBe(canonicalJson(b));
  });

  it('produces different strings for structurally different trees', () => {
    const a = { type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' };
    const b = { type: 'attribute', key: 'plan', cmp: 'eq', value: 'enterprise' };

    expect(canonicalJson(a)).not.toBe(canonicalJson(b));
  });

  it('sorts keys inside array elements too', () => {
    const a = { op: 'or', children: [{ b: 1, a: 2 }] };
    const b = { op: 'or', children: [{ a: 2, b: 1 }] };

    expect(canonicalJson(a)).toBe(canonicalJson(b));
  });
});

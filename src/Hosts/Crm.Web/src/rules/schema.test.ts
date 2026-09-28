import { describe, expect, it } from 'vitest';
import { ruleSchema } from './schema';

function leaf(key = 'k'): unknown {
  return { type: 'attribute', key, cmp: 'exists' };
}

// I-1: `toIssuePath` (the "definition"-rooted error path → Zod issue path translation) had no direct test.
// These pin the three path shapes it produces: root ([]), a top-level child field, and a nested grandchild field.

describe('ruleSchema issue paths (I-1)', () => {
  it('a root-level error (not-two-children style) reports an empty issue path', () => {
    const result = ruleSchema.safeParse({ op: 'not', children: [leaf('a'), leaf('b')] });

    expect(result.success).toBe(false);
    const issue = result.error!.issues[0];
    expect(issue.path).toEqual([]);
    expect(issue.message).toBe('not must have exactly one child');
  });

  it('a bad key at definition.children[1].key reports [children, 1, key]', () => {
    const result = ruleSchema.safeParse({
      op: 'and',
      children: [leaf('plan'), { type: 'attribute', key: 'bad.key', cmp: 'eq', value: 'x' }],
    });

    expect(result.success).toBe(false);
    const issue = result.error!.issues[0];
    expect(issue.path).toEqual(['children', 1, 'key']);
    expect(issue.message).toBe('key must match ^[A-Za-z0-9_]{1,64}$');
  });

  it('a nested error at definition.children[2].children[0].window reports [children, 2, children, 0, window]', () => {
    const result = ruleSchema.safeParse({
      op: 'and',
      children: [
        leaf('a'),
        leaf('b'),
        { op: 'and', children: [{ type: 'event', name: 'purchase', cmp: 'gte', count: 1, window: 'bad' }] },
      ],
    });

    expect(result.success).toBe(false);
    const issue = result.error!.issues[0];
    expect(issue.path).toEqual(['children', 2, 'children', 0, 'window']);
    expect(issue.message).toBe('window must match ^\\d+(d|h)$ and be at most 365 days');
  });
});

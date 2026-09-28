import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { builderReducer, defaultLeaf, errorsFor, newDefinition } from './reducer';
import type { FieldErrors } from './reducer';
import type { AttributeNode, GroupNode, RuleTree } from '@/rules/types';

// Never `import` a fixture: the web Docker build context is `src/Hosts/Crm.Web` only and does not include
// `tests/`. Read with `node:fs` at test time, resolved from `import.meta.url` first — Vite's import-analysis
// plugin rewrites the literal `new URL(relative, import.meta.url)` shape into a dev-server URL (CT-002).
const RULES_ROOT = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  '..',
  '..',
  '..',
  '..',
  '..',
  '..',
  'tests',
  'fixtures',
  'rules',
);

function loadGroupNested(): GroupNode {
  const raw = readFileSync(path.join(RULES_ROOT, 'valid', 'group-nested.json'), 'utf-8');
  return (JSON.parse(raw) as { definition: GroupNode }).definition;
}

describe('defaultLeaf / newDefinition', () => {
  it('defaultLeaf(attribute) is a blank eq condition', () => {
    expect(defaultLeaf('attribute')).toEqual({ type: 'attribute', key: '', cmp: 'eq', value: '' });
  });

  it('defaultLeaf(event) is a blank gte/1/30d condition', () => {
    expect(defaultLeaf('event')).toEqual({ type: 'event', name: '', cmp: 'gte', count: 1, window: '30d' });
  });

  it('newDefinition wraps a default attribute leaf in an and group', () => {
    expect(newDefinition()).toEqual({ op: 'and', children: [defaultLeaf('attribute')] });
  });
});

describe('builderReducer (UT-062)', () => {
  it("setGroupOp replaces only the targeted group's op, keeping sibling identity", () => {
    const tree = loadGroupNested();
    const untouchedSibling = tree.children[0];

    const next = builderReducer(tree, { type: 'setGroupOp', path: [2], op: 'or' }) as GroupNode;
    const nested = next.children[2] as GroupNode;

    expect(nested.op).toBe('or');
    expect(next.children[0]).toBe(untouchedSibling);
    expect(next).not.toBe(tree);
  });

  it("addChild appends to the group at path, keeping the other children's identity", () => {
    const tree = loadGroupNested();
    const untouchedChild1 = tree.children[1];
    const newChild = defaultLeaf('attribute');

    const next = builderReducer(tree, { type: 'addChild', path: [], node: newChild }) as GroupNode;

    expect(next.children).toHaveLength(4);
    expect(next.children[3]).toBe(newChild);
    expect(next.children[1]).toBe(untouchedChild1);
  });

  it('removeNode removes the child at path, keeping the remaining siblings identity, without mutating the input', () => {
    const tree = loadGroupNested();
    const untouchedChild0 = tree.children[0];
    const untouchedChild2 = tree.children[2];

    const next = builderReducer(tree, { type: 'removeNode', path: [1] }) as GroupNode;

    expect(next.children).toHaveLength(2);
    expect(next.children[0]).toBe(untouchedChild0);
    expect(next.children[1]).toBe(untouchedChild2);
    // Purity: the input tree itself must be untouched (guards against an in-place `splice`).
    expect(tree.children).toHaveLength(3);
  });

  it('removeNode with an empty path is a no-op returning the same tree reference', () => {
    const tree = loadGroupNested();
    const next = builderReducer(tree, { type: 'removeNode', path: [] });
    expect(next).toBe(tree);
  });

  it("removeNode of a not's only child leaves an empty not group", () => {
    const notTree: GroupNode = { op: 'not', children: [defaultLeaf('attribute')] };
    const next = builderReducer(notTree, { type: 'removeNode', path: [0] });
    expect(next).toEqual({ op: 'not', children: [] });
  });

  it('moveNode up swaps a child with its previous sibling, keeping untouched siblings identity', () => {
    const tree = loadGroupNested();
    const child1 = tree.children[1];
    const child2 = tree.children[2];
    const untouchedChild0 = tree.children[0];

    const next = builderReducer(tree, { type: 'moveNode', path: [2], direction: 'up' }) as GroupNode;

    expect(next.children[1]).toBe(child2);
    expect(next.children[2]).toBe(child1);
    expect(next.children[0]).toBe(untouchedChild0);
  });

  it('moveNode down swaps a child with its next sibling', () => {
    const tree = loadGroupNested();
    const child0 = tree.children[0];
    const child1 = tree.children[1];

    const next = builderReducer(tree, { type: 'moveNode', path: [0], direction: 'down' }) as GroupNode;

    expect(next.children[0]).toBe(child1);
    expect(next.children[1]).toBe(child0);
  });

  it('moveNode at the top edge is a no-op returning the same tree reference', () => {
    const tree = loadGroupNested();
    const next = builderReducer(tree, { type: 'moveNode', path: [0], direction: 'up' });
    expect(next).toBe(tree);
  });

  it('moveNode at the bottom edge is a no-op returning the same tree reference', () => {
    const tree = loadGroupNested();
    const next = builderReducer(tree, { type: 'moveNode', path: [2], direction: 'down' });
    expect(next).toBe(tree);
  });

  it('updateLeaf replaces a leaf in place, keeping other subtrees identity', () => {
    const tree = loadGroupNested();
    const untouchedChild0 = tree.children[0];
    const nested = tree.children[2] as GroupNode;
    const untouchedNestedSibling = nested.children[1];
    const newLeaf: AttributeNode = { type: 'attribute', key: 'plan', cmp: 'neq', value: 'free' };

    const next = builderReducer(tree, { type: 'updateLeaf', path: [2, 0], leaf: newLeaf }) as GroupNode;
    const nextNested = next.children[2] as GroupNode;

    expect(nextNested.children[0]).toBe(newLeaf);
    expect(nextNested.children[1]).toBe(untouchedNestedSibling);
    expect(next.children[0]).toBe(untouchedChild0);
  });

  it('wrapInGroup wraps the targeted node in a new group, keeping the node identity', () => {
    const tree = loadGroupNested();
    const target = tree.children[0];

    const next = builderReducer(tree, { type: 'wrapInGroup', path: [0], op: 'or' }) as GroupNode;
    const wrapped = next.children[0] as GroupNode;

    expect(wrapped.op).toBe('or');
    expect(wrapped.children[0]).toBe(target);
  });

  it('replace swaps the whole tree', () => {
    const tree = loadGroupNested();
    const other: RuleTree = defaultLeaf('event');
    const next = builderReducer(tree, { type: 'replace', tree: other });
    expect(next).toBe(other);
  });

  it('an edit deep in the tree leaves an unrelated subtree referentially identical', () => {
    const tree = loadGroupNested();
    const nested = tree.children[2] as GroupNode;

    const next = builderReducer(tree, { type: 'updateLeaf', path: [1], leaf: defaultLeaf('event') }) as GroupNode;

    expect(next.children[2]).toBe(nested);
  });
});

describe('errorsFor', () => {
  const errors: FieldErrors = new Map([
    ['definition.children[0].key', ['key must match ^[A-Za-z0-9_]{1,64}$']],
    ['definition.children[2]', ['group must have at least one child']],
  ]);

  it('looks up field errors by path and field', () => {
    expect(errorsFor(errors, [0], 'key')).toEqual(['key must match ^[A-Za-z0-9_]{1,64}$']);
  });

  it('looks up group-level errors by path with no field', () => {
    expect(errorsFor(errors, [2])).toEqual(['group must have at least one child']);
  });

  it('returns an empty array when there is no error at that path/field', () => {
    expect(errorsFor(errors, [1])).toEqual([]);
    expect(errorsFor(errors, [0], 'cmp')).toEqual([]);
  });
});

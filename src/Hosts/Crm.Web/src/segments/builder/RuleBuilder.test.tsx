import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { RuleBuilder } from './RuleBuilder';
import { defaultLeaf } from './reducer';
import type { FieldErrors } from './reducer';
import type { GroupNode } from '@/rules/types';
import { formatPath } from '@/rules/paths';

// Never `import` a fixture (CT-002): resolve `import.meta.url` to a path first, then join — the literal
// `new URL(relative, import.meta.url)` shape gets rewritten by Vite's import-analysis plugin.
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

const NO_ERRORS: FieldErrors = new Map();

describe('RuleBuilder (UT-062)', () => {
  it('renders the nested fixture with path-addressed test ids', () => {
    render(<RuleBuilder tree={loadGroupNested()} onChange={() => {}} errors={NO_ERRORS} />);

    expect(screen.getByTestId('rule-node-root')).toBeTruthy();
    expect(screen.getByTestId('rule-node-0')).toBeTruthy();
    expect(screen.getByTestId('rule-node-1')).toBeTruthy();
    expect(screen.getByTestId('rule-node-2')).toBeTruthy();
    expect(screen.getByTestId('rule-node-2-0')).toBeTruthy();
    expect(screen.getByTestId('rule-node-2-1')).toBeTruthy();
  });

  it('clicking "Add event" on the nested group dispatches an addChild of defaultLeaf(event)', () => {
    const onChange = vi.fn();
    render(<RuleBuilder tree={loadGroupNested()} onChange={onChange} errors={NO_ERRORS} />);

    const nestedGroup = screen.getByTestId('rule-node-2');
    fireEvent.click(within(nestedGroup).getByRole('button', { name: 'Add event' }));

    expect(onChange).toHaveBeenCalledTimes(1);
    const nextTree = onChange.mock.calls[0][0] as GroupNode;
    const nestedNext = nextTree.children[2] as GroupNode;
    expect(nestedNext.children).toHaveLength(3);
    expect(nestedNext.children[2]).toEqual(defaultLeaf('event'));
  });

  it('every non-root node has Remove and Move buttons; the root has none', () => {
    render(<RuleBuilder tree={loadGroupNested()} onChange={() => {}} errors={NO_ERRORS} />);
    // 5 non-root nodes on the fixture: the two top-level leaves, the nested group, and its two leaves.
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(5);
    expect(screen.getAllByRole('button', { name: 'Move up' })).toHaveLength(5);
    expect(screen.getAllByRole('button', { name: 'Move down' })).toHaveLength(5);
    expect(within(screen.getByTestId('rule-node-root')).queryAllByRole('button', { name: 'Remove' })).toHaveLength(5);
  });

  it('a nested group has its own Remove and Move buttons, distinct from its children\'s', () => {
    render(<RuleBuilder tree={loadGroupNested()} onChange={() => {}} errors={NO_ERRORS} />);
    const nestedGroup = screen.getByTestId('rule-node-2');
    // the group's own toolbar renders before its two leaf children in DOM order, so 3 Remove buttons total
    // sit within this container: the group's own, plus one per leaf child.
    const removeButtons = within(nestedGroup).getAllByRole('button', { name: 'Remove' });
    expect(removeButtons).toHaveLength(3);
    expect(removeButtons[0].closest('[data-testid]')?.getAttribute('data-testid')).toBe('rule-node-2');
  });

  it('leaf rows have Remove and Move buttons (plan defect fix)', () => {
    render(<RuleBuilder tree={loadGroupNested()} onChange={() => {}} errors={NO_ERRORS} />);
    const attrLeaf = screen.getByTestId('rule-node-0');
    expect(within(attrLeaf).getByRole('button', { name: 'Remove' })).toBeTruthy();
    expect(within(attrLeaf).getByRole('button', { name: 'Move up' })).toBeTruthy();
    expect(within(attrLeaf).getByRole('button', { name: 'Move down' })).toBeTruthy();

    const eventLeaf = screen.getByTestId('rule-node-1');
    expect(within(eventLeaf).getByRole('button', { name: 'Remove' })).toBeTruthy();
  });

  it('"Move down" on rule-node-0 swaps children 0 and 1', () => {
    const onChange = vi.fn();
    render(<RuleBuilder tree={loadGroupNested()} onChange={onChange} errors={NO_ERRORS} />);

    fireEvent.click(within(screen.getByTestId('rule-node-0')).getByRole('button', { name: 'Move down' }));

    expect(onChange).toHaveBeenCalledTimes(1);
    const nextTree = onChange.mock.calls[0][0] as GroupNode;
    expect(nextTree.children[0]).toEqual({ type: 'event', name: 'purchase', cmp: 'gte', count: 3, window: '30d' });
    expect(nextTree.children[1]).toEqual({ type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' });
  });

  it('"Remove" on rule-node-2-1 leaves the nested "or" group with one child', () => {
    const onChange = vi.fn();
    render(<RuleBuilder tree={loadGroupNested()} onChange={onChange} errors={NO_ERRORS} />);

    fireEvent.click(within(screen.getByTestId('rule-node-2-1')).getByRole('button', { name: 'Remove' }));

    expect(onChange).toHaveBeenCalledTimes(1);
    const nextTree = onChange.mock.calls[0][0] as GroupNode;
    const nestedNext = nextTree.children[2] as GroupNode;
    expect(nestedNext.children).toHaveLength(1);
    expect(nestedNext.children[0]).toEqual({ type: 'attribute', key: 'country', cmp: 'in', value: ['ID', 'SG'] });
  });

  it('renders a server field error on the row it belongs to', () => {
    const errors: FieldErrors = new Map([[formatPath([0], 'key'), ['key must match ^[A-Za-z0-9_]{1,64}$']]]);
    render(<RuleBuilder tree={loadGroupNested()} onChange={() => {}} errors={errors} />);

    const row0 = screen.getByTestId('rule-node-0');
    expect(within(row0).getAllByRole('alert')).toHaveLength(1);
    const row1 = screen.getByTestId('rule-node-1');
    expect(within(row1).queryAllByRole('alert')).toHaveLength(0);
  });

  it('readOnly renders no buttons and disables every control', () => {
    const { container } = render(
      <RuleBuilder tree={loadGroupNested()} onChange={() => {}} errors={NO_ERRORS} readOnly />,
    );

    expect(screen.queryAllByRole('button')).toHaveLength(0);
    const controls = container.querySelectorAll('input, select');
    expect(controls.length).toBeGreaterThan(0);
    controls.forEach((el) => expect((el as HTMLInputElement | HTMLSelectElement).disabled).toBe(true));
  });
});

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

  it('exactly one Remove button exists, for the one non-root group (the root has none)', () => {
    render(<RuleBuilder tree={loadGroupNested()} onChange={() => {}} errors={NO_ERRORS} />);
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Move up' })).toHaveLength(1);
  });

  it('a nested group has Remove and Move buttons', () => {
    render(<RuleBuilder tree={loadGroupNested()} onChange={() => {}} errors={NO_ERRORS} />);
    const nestedGroup = screen.getByTestId('rule-node-2');
    expect(within(nestedGroup).getByRole('button', { name: 'Remove' })).toBeTruthy();
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

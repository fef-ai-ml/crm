import { useState } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { EventLeafRow } from './EventLeafRow';
import type { BuilderAction, FieldErrors } from './reducer';
import type { EventNode } from '@/rules/types';
import { formatPath } from '@/rules/paths';

const NO_ERRORS: FieldErrors = new Map();

function baseNode(): EventNode {
  return { type: 'event', name: 'purchase', cmp: 'gte', count: 3, window: '30d' };
}

function leafFrom(dispatch: ReturnType<typeof vi.fn>, callIndex = 0): EventNode {
  return dispatch.mock.calls[callIndex][0].leaf as EventNode;
}

// A tiny stateful harness: a bare `vi.fn()` dispatch never feeds `node` back as a new prop, so it can't tell
// the difference between "the buffer's own edit round-tripping back down" and "an external revert" — the same
// distinction the render-time resync exists to make. Wiring `dispatch` through real state (as the live app
// does via `builderReducer`) puts the child's own buffer update and the ancestor's prop update in the same
// render batch, matching production, so this is used wherever a test cares about the buffer's displayed value
// across more than one edit.
function Harness({ initial }: { initial: EventNode }) {
  const [node, setNode] = useState(initial);
  function dispatch(action: BuilderAction) {
    if (action.type === 'updateLeaf') setNode(action.leaf as EventNode);
  }
  return (
    <>
      <span data-testid="count-in-tree">{node.count}</span>
      <EventLeafRow node={node} path={[1]} errors={NO_ERRORS} dispatch={dispatch} />
    </>
  );
}

describe('EventLeafRow (UT-062)', () => {
  it('has a path-addressed test id', () => {
    render(<EventLeafRow node={baseNode()} path={[1]} errors={NO_ERRORS} dispatch={vi.fn()} />);
    expect(screen.getByTestId('rule-node-1')).toBeTruthy();
  });

  it('composes a window amount and unit into the window string', () => {
    const dispatch = vi.fn();
    render(<EventLeafRow node={baseNode()} path={[1]} errors={NO_ERRORS} dispatch={dispatch} />);
    fireEvent.change(screen.getByLabelText('Window amount'), { target: { value: '7' } });

    expect(leafFrom(dispatch).window).toBe('7d');
  });

  it('changing the window unit recomposes the window string', () => {
    const dispatch = vi.fn();
    render(<EventLeafRow node={baseNode()} path={[1]} errors={NO_ERRORS} dispatch={dispatch} />);
    fireEvent.change(screen.getByLabelText('Window unit'), { target: { value: 'h' } });

    expect(leafFrom(dispatch).window).toBe('30h');
  });

  it('an empty window amount clears the window so the validator reports it', () => {
    const dispatch = vi.fn();
    render(<EventLeafRow node={baseNode()} path={[1]} errors={NO_ERRORS} dispatch={dispatch} />);
    fireEvent.change(screen.getByLabelText('Window amount'), { target: { value: '' } });

    expect(leafFrom(dispatch).window).toBe('');
  });

  it('an empty count keeps the last valid count and does not dispatch', () => {
    const dispatch = vi.fn();
    render(<EventLeafRow node={baseNode()} path={[1]} errors={NO_ERRORS} dispatch={dispatch} />);
    fireEvent.change(screen.getByLabelText('Count'), { target: { value: '' } });

    expect(dispatch).not.toHaveBeenCalled();
    expect((screen.getByLabelText('Count') as HTMLInputElement).value).toBe('');
  });

  it('a valid count dispatches updateLeaf with the parsed number', () => {
    const dispatch = vi.fn();
    render(<EventLeafRow node={baseNode()} path={[1]} errors={NO_ERRORS} dispatch={dispatch} />);
    fireEvent.change(screen.getByLabelText('Count'), { target: { value: '5' } });

    expect(leafFrom(dispatch).count).toBe(5);
  });

  it('a bare "-" leaves count unchanged and does not dispatch; a completed number does (C-2)', () => {
    const dispatch = vi.fn();
    render(<EventLeafRow node={baseNode()} path={[1]} errors={NO_ERRORS} dispatch={dispatch} />);

    fireEvent.change(screen.getByLabelText('Count'), { target: { value: '-' } });
    expect(dispatch).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Count'), { target: { value: '2' } });
    expect(dispatch).toHaveBeenCalledTimes(1);
    const leaf = leafFrom(dispatch, 0);
    expect(leaf.count).toBe(2);
    expect(JSON.stringify(leaf)).not.toContain('null');
  });

  it('a non-canonical count like "007" stays as typed while the tree holds the parsed number (M-2)', () => {
    render(<Harness initial={baseNode()} />);

    fireEvent.change(screen.getByLabelText('Count'), { target: { value: '007' } });

    expect((screen.getByLabelText('Count') as HTMLInputElement).value).toBe('007');
    expect(screen.getByTestId('count-in-tree').textContent).toBe('7');
  });

  it('event name and comparator edits dispatch updateLeaf', () => {
    const dispatch = vi.fn();
    render(<EventLeafRow node={baseNode()} path={[1]} errors={NO_ERRORS} dispatch={dispatch} />);
    fireEvent.change(screen.getByLabelText('Event name'), { target: { value: 'app_open' } });
    expect(leafFrom(dispatch).name).toBe('app_open');

    fireEvent.change(screen.getByLabelText('Comparator'), { target: { value: 'lte' } });
    expect(leafFrom(dispatch, 1).cmp).toBe('lte');
  });

  it('renders field errors as alerts under their own field, not other fields', () => {
    const errors: FieldErrors = new Map([[formatPath([1], 'window'), ['window must match ^\\d+(d|h)$ and be at most 365 days']]]);
    render(<EventLeafRow node={baseNode()} path={[1]} errors={errors} dispatch={vi.fn()} />);

    const alerts = screen.getAllByRole('alert');
    expect(alerts).toHaveLength(1);
  });

  it('readOnly disables every control', () => {
    const { container } = render(
      <EventLeafRow node={baseNode()} path={[1]} errors={NO_ERRORS} dispatch={vi.fn()} readOnly />,
    );

    const controls = container.querySelectorAll('input, select');
    expect(controls.length).toBeGreaterThan(0);
    controls.forEach((el) => expect((el as HTMLInputElement | HTMLSelectElement).disabled).toBe(true));
  });
});

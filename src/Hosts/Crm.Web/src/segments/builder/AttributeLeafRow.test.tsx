import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AttributeLeafRow } from './AttributeLeafRow';
import type { AttributeNode } from '@/rules/types';
import type { FieldErrors } from './reducer';
import { formatPath } from '@/rules/paths';

const NO_ERRORS: FieldErrors = new Map();

function renderRow(node: AttributeNode, errors: FieldErrors = NO_ERRORS, readOnly = false) {
  const dispatch = vi.fn();
  const utils = render(
    <AttributeLeafRow node={node} path={[0]} errors={errors} dispatch={dispatch} readOnly={readOnly} />,
  );
  return {
    dispatch,
    rerender: (next: AttributeNode) =>
      utils.rerender(
        <AttributeLeafRow node={next} path={[0]} errors={errors} dispatch={dispatch} readOnly={readOnly} />,
      ),
  };
}

function leafFrom(dispatch: ReturnType<typeof vi.fn>, callIndex = 0): AttributeNode {
  return dispatch.mock.calls[callIndex][0].leaf as AttributeNode;
}

describe('AttributeLeafRow (UT-062)', () => {
  it('has a path-addressed test id', () => {
    render(<AttributeLeafRow node={{ type: 'attribute', key: 'k', cmp: 'eq', value: '' }} path={[2, 0]} errors={NO_ERRORS} dispatch={vi.fn()} />);
    expect(screen.getByTestId('rule-node-2-0')).toBeTruthy();
  });

  it('switching the comparator to exists removes the value key entirely', () => {
    const { dispatch } = renderRow({ type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' });
    fireEvent.change(screen.getByLabelText('Comparator'), { target: { value: 'exists' } });

    const leaf = leafFrom(dispatch);
    expect(Object.hasOwn(leaf, 'value')).toBe(false);
    expect(leaf).toEqual({ type: 'attribute', key: 'plan', cmp: 'exists' });
  });

  it('switching the comparator to not_exists removes the value key entirely', () => {
    const { dispatch } = renderRow({ type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' });
    fireEvent.change(screen.getByLabelText('Comparator'), { target: { value: 'not_exists' } });

    const leaf = leafFrom(dispatch);
    expect(Object.hasOwn(leaf, 'value')).toBe(false);
  });

  it('switching from eq (text) to contains keeps the string value', () => {
    const { dispatch } = renderRow({ type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' });
    fireEvent.change(screen.getByLabelText('Comparator'), { target: { value: 'contains' } });

    expect(leafFrom(dispatch).value).toBe('pro');
  });

  it('switching to gt resets a non-numeric value to no value (empty number input)', () => {
    const { dispatch } = renderRow({ type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' });
    fireEvent.change(screen.getByLabelText('Comparator'), { target: { value: 'gt' } });

    expect(Object.hasOwn(leafFrom(dispatch), 'value')).toBe(false);
  });

  it('switching to in resets to an empty list', () => {
    const { dispatch } = renderRow({ type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' });
    fireEvent.change(screen.getByLabelText('Comparator'), { target: { value: 'in' } });

    expect(leafFrom(dispatch).value).toEqual([]);
  });

  it('"in" parses a comma-separated list into strings', () => {
    const { dispatch } = renderRow({ type: 'attribute', key: 'country', cmp: 'in', value: [] });
    fireEvent.change(screen.getByLabelText('Value'), { target: { value: 'ID, SG' } });

    expect(leafFrom(dispatch).value).toEqual(['ID', 'SG']);
  });

  it('typing an "in" list character by character never loses a keystroke (C-1)', () => {
    const { dispatch, rerender } = renderRow({ type: 'attribute', key: 'country', cmp: 'in', value: [] });
    const value = () => (screen.getByLabelText('Value') as HTMLInputElement).value;

    // Simulates typing "ID, SG" one character at a time: each `fireEvent.change` carries the full string typed
    // so far (as successive keystrokes would leave the input), then the row is re-rendered with the freshly
    // dispatched leaf — mirroring the real reducer round trip a live app performs in the same render batch.
    for (const typed of ['I', 'ID', 'ID,', 'ID, ', 'ID, S', 'ID, SG']) {
      fireEvent.change(screen.getByLabelText('Value'), { target: { value: typed } });
      rerender(leafFrom(dispatch, dispatch.mock.calls.length - 1));
      expect(value()).toBe(typed);
    }
    expect(leafFrom(dispatch, dispatch.mock.calls.length - 1).value).toEqual(['ID', 'SG']);

    // a trailing comma stays visible without losing the already-parsed items
    fireEvent.change(screen.getByLabelText('Value'), { target: { value: 'ID, SG,' } });
    rerender(leafFrom(dispatch, dispatch.mock.calls.length - 1));
    expect(value()).toBe('ID, SG,');
    expect(leafFrom(dispatch, dispatch.mock.calls.length - 1).value).toEqual(['ID', 'SG']);

    // an external value change (not derived from what's currently typed) replaces the text
    rerender({ type: 'attribute', key: 'country', cmp: 'in', value: ['X'] });
    expect(value()).toBe('X');
  });

  it('"in" parses a comma-separated list into numbers when every item is numeric', () => {
    const { dispatch } = renderRow({ type: 'attribute', key: 'score', cmp: 'in', value: [] });
    fireEvent.change(screen.getByLabelText('Value'), { target: { value: '1, 2.5' } });

    expect(leafFrom(dispatch).value).toEqual([1, 2.5]);
  });

  it('a number comparator with an empty value removes the value key', () => {
    const { dispatch } = renderRow({ type: 'attribute', key: 'age', cmp: 'gt', value: 5 });
    fireEvent.change(screen.getByLabelText('Value'), { target: { value: '' } });

    expect(Object.hasOwn(leafFrom(dispatch), 'value')).toBe(false);
  });

  it('a bare "-" never writes NaN into the tree; a completed negative number does (C-2)', () => {
    const { dispatch, rerender } = renderRow({ type: 'attribute', key: 'age', cmp: 'lt', value: 5 });

    fireEvent.change(screen.getByLabelText('Value'), { target: { value: '-' } });
    expect(dispatch).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Value'), { target: { value: '-1' } });
    expect(dispatch).toHaveBeenCalledTimes(1);
    const leaf = leafFrom(dispatch, 0);
    expect(leaf.value).toBe(-1);
    expect(JSON.stringify(leaf)).not.toContain('null');

    rerender(leaf);
    expect((screen.getByLabelText('Value') as HTMLInputElement).value).toBe('-1');
  });

  it('an incomplete exponent ("1e") never writes NaN into an eq-number value', () => {
    const { dispatch, rerender } = renderRow({ type: 'attribute', key: 'age', cmp: 'eq', value: 1 });
    fireEvent.change(screen.getByLabelText('Value type'), { target: { value: 'number' } });
    rerender(leafFrom(dispatch, 0));

    fireEvent.change(screen.getByLabelText('Value'), { target: { value: '1e' } });
    expect(dispatch).toHaveBeenCalledTimes(1); // only the type switch, no NaN dispatch from "1e"

    fireEvent.change(screen.getByLabelText('Value'), { target: { value: '1e3' } });
    expect(leafFrom(dispatch, 1).value).toBe(1000);
  });

  it('eq with type number and text "42" yields the number 42', () => {
    const { dispatch, rerender } = renderRow({ type: 'attribute', key: 'age', cmp: 'eq', value: '' });

    fireEvent.change(screen.getByLabelText('Value type'), { target: { value: 'number' } });
    const afterType = leafFrom(dispatch, 0);
    expect(afterType.value).toBe(0);

    rerender(afterType);
    fireEvent.change(screen.getByLabelText('Value'), { target: { value: '42' } });
    expect(leafFrom(dispatch, 1).value).toBe(42);
  });

  it('eq with type boolean yields a boolean value', () => {
    const { dispatch, rerender } = renderRow({ type: 'attribute', key: 'active', cmp: 'eq', value: '' });

    fireEvent.change(screen.getByLabelText('Value type'), { target: { value: 'boolean' } });
    const afterType = leafFrom(dispatch, 0);
    expect(afterType.value).toBe(false);

    rerender(afterType);
    fireEvent.change(screen.getByLabelText('Value'), { target: { value: 'true' } });
    expect(leafFrom(dispatch, 1).value).toBe(true);
  });

  it('a value-keyed error on exists/not_exists renders under the comparator, not as a floating alert (M-1)', () => {
    const errors: FieldErrors = new Map([[formatPath([0], 'value'), ['value is not allowed for this comparator']]]);
    render(
      <AttributeLeafRow
        node={{ type: 'attribute', key: 'plan', cmp: 'exists' }}
        path={[0]}
        errors={errors}
        dispatch={vi.fn()}
      />,
    );

    const alerts = screen.getAllByRole('alert');
    expect(alerts).toHaveLength(1);
    expect(alerts[0].textContent).toBe('value is not allowed for this comparator');
    // no value control exists for exists/not_exists, so the alert must sit next to the comparator select
    expect(screen.getByLabelText('Comparator').parentElement?.contains(alerts[0])).toBe(true);
  });

  it('renders field errors as alerts under their own field, not other fields', () => {
    const errors: FieldErrors = new Map([[formatPath([0], 'key'), ['key must match ^[A-Za-z0-9_]{1,64}$']]]);
    render(
      <AttributeLeafRow
        node={{ type: 'attribute', key: 'bad key', cmp: 'eq', value: 'x' }}
        path={[0]}
        errors={errors}
        dispatch={vi.fn()}
      />,
    );

    const alerts = screen.getAllByRole('alert');
    expect(alerts).toHaveLength(1);
    expect(alerts[0].textContent).toBe('key must match ^[A-Za-z0-9_]{1,64}$');
  });

  it('readOnly disables every control', () => {
    const { container } = render(
      <AttributeLeafRow
        node={{ type: 'attribute', key: 'plan', cmp: 'eq', value: 'pro' }}
        path={[0]}
        errors={NO_ERRORS}
        dispatch={vi.fn()}
        readOnly
      />,
    );

    const controls = container.querySelectorAll('input, select');
    expect(controls.length).toBeGreaterThan(0);
    controls.forEach((el) => expect((el as HTMLInputElement | HTMLSelectElement).disabled).toBe(true));
  });
});

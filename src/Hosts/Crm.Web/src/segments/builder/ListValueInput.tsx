import { useState, type ChangeEvent } from 'react';
import { Input } from '@/components/ui/input';

/** `in`/`nin` values are edited as one comma-separated text field (J17): split on `,`, trim, drop empties;
 * `string[]` unless every trimmed item is a finite number, in which case `number[]`; `[]` when empty. A value
 * containing a comma cannot be entered — a documented limit, not a bug. */
export type ListValue = string[] | number[];

function parseList(raw: string): ListValue {
  const items = raw
    .split(',')
    .map((item) => item.trim())
    .filter((item) => item.length > 0);

  if (items.length === 0) return [];

  const allNumbers = items.every((item) => item.length > 0 && Number.isFinite(Number(item)));
  return allNumbers ? items.map(Number) : items;
}

export type ListValueInputProps = {
  value: ListValue;
  onChange: (value: ListValue) => void;
  disabled?: boolean;
};

export function ListValueInput({ value, onChange, disabled }: ListValueInputProps) {
  const [text, setText] = useState(() => value.join(', '));
  const [prevValue, setPrevValue] = useState(value);

  // Resyncs `text` when `value` changes from the outside (e.g. a comparator switch resetting it) by adjusting
  // state during render rather than in an effect (avoids react-hooks/set-state-in-effect's cascading render);
  // a keystroke's own `onChange` already reflects `text`, so this never fights the user mid-edit.
  if (value !== prevValue) {
    setPrevValue(value);
    setText(value.join(', '));
  }

  function handleChange(event: ChangeEvent<HTMLInputElement>) {
    const raw = event.target.value;
    setText(raw);
    onChange(parseList(raw));
  }

  return <Input aria-label="Value" value={text} onChange={handleChange} disabled={disabled} className="w-48" />;
}

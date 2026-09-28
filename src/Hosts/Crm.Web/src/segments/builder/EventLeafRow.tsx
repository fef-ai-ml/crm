import { useState, type ChangeEvent } from 'react';
import { Input } from '@/components/ui/input';
import { Select, type SelectOption } from '@/components/ui/select';
import { WINDOW_PATTERN } from '@/rules/types';
import { errorsFor, testIdFor } from './reducer';
import type { BuilderAction, FieldErrors } from './reducer';
import type { EventCmp, EventNode } from '@/rules/types';
import type { NodePath } from '@/rules/paths';

const EVENT_CMPS: SelectOption[] = [
  { value: 'eq', label: 'Equals' },
  { value: 'gte', label: 'At least' },
  { value: 'lte', label: 'At most' },
];

const WINDOW_UNITS: SelectOption[] = [
  { value: 'd', label: 'Days' },
  { value: 'h', label: 'Hours' },
];

export type EventLeafRowProps = {
  node: EventNode;
  path: NodePath;
  errors: FieldErrors;
  dispatch: (action: BuilderAction) => void;
  readOnly?: boolean;
};

export function EventLeafRow({ node, path, errors, dispatch, readOnly = false }: EventLeafRowProps) {
  // "Count" buffers its own text so the field can go visually empty while typing without losing the last
  // valid `count` on the node (an empty string is not a valid `number`) — the controller resolution. Resynced
  // by adjusting state during render (not in an effect, which would cause a cascading render) whenever `count`
  // changes from the outside.
  const [countText, setCountText] = useState(String(node.count));
  const [prevCount, setPrevCount] = useState(node.count);
  if (node.count !== prevCount) {
    setPrevCount(node.count);
    setCountText(String(node.count));
  }

  function emit(next: EventNode) {
    dispatch({ type: 'updateLeaf', path, leaf: next });
  }

  function handleNameChange(event: ChangeEvent<HTMLInputElement>) {
    emit({ ...node, name: event.target.value });
  }

  function handleCmpChange(event: ChangeEvent<HTMLSelectElement>) {
    emit({ ...node, cmp: event.target.value as EventCmp });
  }

  function handleCountChange(event: ChangeEvent<HTMLInputElement>) {
    const raw = event.target.value;
    setCountText(raw);
    if (raw === '') return; // keep the last valid count; a non-integer string is left to the validator
    emit({ ...node, count: Number(raw) });
  }

  const windowMatch = WINDOW_PATTERN.exec(node.window);
  const amount = windowMatch ? windowMatch[1] : '';
  const unit = (windowMatch ? windowMatch[2] : 'd') as 'd' | 'h';

  function handleAmountChange(event: ChangeEvent<HTMLInputElement>) {
    const raw = event.target.value;
    emit({ ...node, window: raw === '' ? '' : `${raw}${unit}` }); // empty amount -> the validator reports it
  }

  function handleUnitChange(event: ChangeEvent<HTMLSelectElement>) {
    const nextUnit = event.target.value as 'd' | 'h';
    emit({ ...node, window: amount === '' ? '' : `${amount}${nextUnit}` });
  }

  const nameErrors = errorsFor(errors, path, 'name');
  const cmpErrors = errorsFor(errors, path, 'cmp');
  const countErrors = errorsFor(errors, path, 'count');
  const windowErrors = errorsFor(errors, path, 'window');

  return (
    <div data-testid={`rule-node-${testIdFor(path)}`} className="flex flex-wrap items-start gap-2">
      <div className="flex flex-col gap-1">
        <Input
          aria-label="Event name"
          value={node.name}
          onChange={handleNameChange}
          disabled={readOnly}
          className="w-36"
        />
        {nameErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
      </div>

      <div className="flex flex-col gap-1">
        <Select
          aria-label="Comparator"
          options={EVENT_CMPS}
          value={node.cmp}
          onChange={handleCmpChange}
          disabled={readOnly}
          className="w-32"
        />
        {cmpErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
      </div>

      <div className="flex flex-col gap-1">
        <Input
          type="number"
          min={0}
          step={1}
          aria-label="Count"
          value={countText}
          onChange={handleCountChange}
          disabled={readOnly}
          className="w-24"
        />
        {countErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
      </div>

      <div className="flex flex-col gap-1">
        <div className="flex items-center gap-2">
          <Input
            type="number"
            aria-label="Window amount"
            value={amount}
            onChange={handleAmountChange}
            disabled={readOnly}
            className="w-20"
          />
          <Select
            aria-label="Window unit"
            options={WINDOW_UNITS}
            value={unit}
            onChange={handleUnitChange}
            disabled={readOnly}
            className="w-24"
          />
        </div>
        {windowErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
      </div>
    </div>
  );
}

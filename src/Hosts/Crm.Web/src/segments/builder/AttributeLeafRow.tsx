import { useState, type ChangeEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, type SelectOption } from '@/components/ui/select';
import { ListValueInput, type ListValue } from './ListValueInput';
import { errorsFor, testIdFor } from './reducer';
import type { BuilderAction, FieldErrors } from './reducer';
import type { AttributeCmp, AttributeNode, RuleValue } from '@/rules/types';
import type { NodePath } from '@/rules/paths';

const ATTRIBUTE_CMPS: SelectOption[] = [
  { value: 'eq', label: 'Equals' },
  { value: 'neq', label: 'Not equals' },
  { value: 'in', label: 'In' },
  { value: 'nin', label: 'Not in' },
  { value: 'gt', label: 'Greater than' },
  { value: 'gte', label: 'Greater than or equal' },
  { value: 'lt', label: 'Less than' },
  { value: 'lte', label: 'Less than or equal' },
  { value: 'contains', label: 'Contains' },
  { value: 'exists', label: 'Exists' },
  { value: 'not_exists', label: 'Does not exist' },
];

const VALUE_TYPES: SelectOption[] = [
  { value: 'text', label: 'Text' },
  { value: 'number', label: 'Number' },
  { value: 'boolean', label: 'Boolean' },
];

/** Whether `value`'s current shape is still valid for `cmp` — governs whether a comparator switch keeps it
 * (J5 controller resolution: "keeps the value when its kind still fits ... else resets to the default"). */
function fitsCmp(cmp: AttributeCmp, value: RuleValue | undefined): boolean {
  switch (cmp) {
    case 'exists':
    case 'not_exists':
      return value === undefined;
    case 'gt':
    case 'gte':
    case 'lt':
    case 'lte':
      return typeof value === 'number';
    case 'contains':
      return typeof value === 'string';
    case 'in':
    case 'nin':
      return Array.isArray(value);
    case 'eq':
    case 'neq':
      return typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean';
  }
}

function defaultValueForCmp(cmp: AttributeCmp): RuleValue | undefined {
  switch (cmp) {
    case 'exists':
    case 'not_exists':
    case 'gt':
    case 'gte':
    case 'lt':
    case 'lte':
      return undefined;
    case 'in':
    case 'nin':
      return [];
    case 'eq':
    case 'neq':
    case 'contains':
      return '';
  }
}

function nextAttributeValue(cmp: AttributeCmp, current: RuleValue | undefined): RuleValue | undefined {
  return fitsCmp(cmp, current) ? current : defaultValueForCmp(cmp);
}

type Base = { type: 'attribute'; key: string; cmp: AttributeCmp };

// `exists`/`not_exists` DELETE the `value` key: never `value: undefined`, which `Object.hasOwn` would still
// count as present (the validator would then say "value is not allowed") — J5 node shape discipline.
function withValue(base: Base, value: RuleValue | undefined): AttributeNode {
  return value === undefined ? { ...base } : { ...base, value };
}

function typeOfValue(value: RuleValue | undefined): 'text' | 'number' | 'boolean' {
  if (typeof value === 'number') return 'number';
  if (typeof value === 'boolean') return 'boolean';
  return 'text';
}

type NumberFieldProps = {
  value: number | undefined;
  onValue: (value: number | undefined) => void;
  emptyValue: number | undefined;
  disabled: boolean;
  className: string;
};

// Buffers typed text so an in-progress numeric edit (a bare `-`, `-0.`, `1e`, `.`) never writes into the tree
// (C-2): `Number('-')` is `NaN`, and `NaN` would silently survive further edits because `typeof NaN ===
// 'number'` passes the same duck-typed checks (`fitsCmp`) a real number would. Rendered as `type="text"` with
// `inputMode="decimal"`, not `type="number"`: a native number input's `value` IDL attribute runs the HTML
// value-sanitization algorithm and silently collapses any syntactically invalid intermediate ("-", "-0.",
// "1e", ".") to the empty string before our `onChange` ever sees it — indistinguishable from the user clearing
// the field — so our own `Number.isFinite` guard would never even get a chance to run on the text that
// actually needs it. A plain text input hands us the raw keystrokes and keeps the numeric keyboard on mobile
// via `inputMode`. Resynced by adjusting state during render, compared by CONTENT (what the buffer currently
// parses to) against the incoming `value` — the same principle as `ListValueInput` (C-1) — so our own dispatch
// of a just-typed value never fights the buffer, and an indeterminate in-progress text (empty, or not a finite
// number) never forces a resync either.
function NumberField({ value, onValue, emptyValue, disabled, className }: NumberFieldProps) {
  const [text, setText] = useState(() => (value === undefined ? '' : String(value)));

  if (text.trim() === '') {
    if (value !== emptyValue) setText(value === undefined ? '' : String(value));
  } else {
    const parsed = Number(text);
    if (Number.isFinite(parsed) && parsed !== value) setText(value === undefined ? '' : String(value));
  }

  function handleChange(event: ChangeEvent<HTMLInputElement>) {
    const raw = event.target.value;
    setText(raw);

    if (raw.trim() === '') {
      onValue(emptyValue);
      return;
    }

    const parsed = Number(raw);
    if (!Number.isFinite(parsed)) return; // e.g. "-", "-0.", "1e", ".": keep typing, tree stays untouched
    onValue(parsed);
  }

  return (
    <Input
      type="text"
      inputMode="decimal"
      aria-label="Value"
      value={text}
      onChange={handleChange}
      disabled={disabled}
      className={className}
    />
  );
}

export type AttributeLeafRowProps = {
  node: AttributeNode;
  path: NodePath;
  errors: FieldErrors;
  dispatch: (action: BuilderAction) => void;
  readOnly?: boolean;
  isRoot?: boolean;
};

export function AttributeLeafRow({
  node,
  path,
  errors,
  dispatch,
  readOnly = false,
  isRoot = false,
}: AttributeLeafRowProps) {
  function emit(next: AttributeNode) {
    dispatch({ type: 'updateLeaf', path, leaf: next });
  }

  function base(overrides: Partial<Base> = {}): Base {
    return { type: 'attribute', key: node.key, cmp: node.cmp, ...overrides };
  }

  function handleKeyChange(event: ChangeEvent<HTMLInputElement>) {
    emit(withValue(base({ key: event.target.value }), node.value));
  }

  function handleCmpChange(event: ChangeEvent<HTMLSelectElement>) {
    const cmp = event.target.value as AttributeCmp;
    emit(withValue(base({ cmp }), nextAttributeValue(cmp, node.value)));
  }

  function handleValue(value: RuleValue | undefined) {
    emit(withValue(base(), value));
  }

  const keyErrors = errorsFor(errors, path, 'key');
  const cmpErrors = errorsFor(errors, path, 'cmp');
  const valueErrors = errorsFor(errors, path, 'value');
  // `exists`/`not_exists` render no value control at all (M-1): a `value`-keyed error for one of them has no
  // input to sit under, so it renders under the comparator instead of floating with nothing above it.
  const isValueless = node.cmp === 'exists' || node.cmp === 'not_exists';

  return (
    <div data-testid={`rule-node-${testIdFor(path)}`} className="flex flex-wrap items-start gap-2">
      <div className="flex flex-col gap-1">
        <Input aria-label="Key" value={node.key} onChange={handleKeyChange} disabled={readOnly} className="w-36" />
        {keyErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
      </div>

      <div className="flex flex-col gap-1">
        <Select
          aria-label="Comparator"
          options={ATTRIBUTE_CMPS}
          value={node.cmp}
          onChange={handleCmpChange}
          disabled={readOnly}
          className="w-48"
        />
        {cmpErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
        {isValueless &&
          valueErrors.map((message) => (
            <p role="alert" key={`value-${message}`} className="text-xs text-red-600">
              {message}
            </p>
          ))}
      </div>

      <div className="flex flex-col gap-1">
        <ValueControl node={node} readOnly={readOnly} onValue={handleValue} />
        {!isValueless &&
          valueErrors.map((message) => (
            <p role="alert" key={message} className="text-xs text-red-600">
              {message}
            </p>
          ))}
      </div>

      {!readOnly && !isRoot && (
        <div className="flex items-center gap-1">
          <Button type="button" size="sm" variant="ghost" onClick={() => dispatch({ type: 'removeNode', path })}>
            Remove
          </Button>
          <Button
            type="button"
            size="sm"
            variant="ghost"
            aria-label="Move up"
            onClick={() => dispatch({ type: 'moveNode', path, direction: 'up' })}
          >
            ↑
          </Button>
          <Button
            type="button"
            size="sm"
            variant="ghost"
            aria-label="Move down"
            onClick={() => dispatch({ type: 'moveNode', path, direction: 'down' })}
          >
            ↓
          </Button>
        </div>
      )}
    </div>
  );
}

function ValueControl({
  node,
  readOnly,
  onValue,
}: {
  node: AttributeNode;
  readOnly: boolean;
  onValue: (value: RuleValue | undefined) => void;
}) {
  switch (node.cmp) {
    case 'exists':
    case 'not_exists':
      return null;

    case 'gt':
    case 'gte':
    case 'lt':
    case 'lte':
      return (
        <NumberField
          value={typeof node.value === 'number' ? node.value : undefined}
          onValue={onValue}
          emptyValue={undefined}
          disabled={readOnly}
          className="w-32"
        />
      );

    case 'contains':
      return (
        <Input
          aria-label="Value"
          value={typeof node.value === 'string' ? node.value : ''}
          onChange={(event) => onValue(event.target.value)}
          disabled={readOnly}
          className="w-40"
        />
      );

    case 'in':
    case 'nin':
      return (
        <ListValueInput
          value={Array.isArray(node.value) ? (node.value as ListValue) : []}
          onChange={onValue}
          disabled={readOnly}
        />
      );

    case 'eq':
    case 'neq': {
      const currentType = typeOfValue(node.value);
      return (
        <div className="flex items-center gap-2">
          <Select
            aria-label="Value type"
            options={VALUE_TYPES}
            value={currentType}
            onChange={(event) => {
              const type = event.target.value as 'text' | 'number' | 'boolean';
              onValue(type === 'number' ? 0 : type === 'boolean' ? false : '');
            }}
            disabled={readOnly}
            className="w-28"
          />
          {currentType === 'text' && (
            <Input
              aria-label="Value"
              value={typeof node.value === 'string' ? node.value : ''}
              onChange={(event) => onValue(event.target.value)}
              disabled={readOnly}
              className="w-40"
            />
          )}
          {currentType === 'number' && (
            <NumberField
              value={typeof node.value === 'number' ? node.value : 0}
              onValue={onValue}
              emptyValue={0}
              disabled={readOnly}
              className="w-32"
            />
          )}
          {currentType === 'boolean' && (
            <Select
              aria-label="Value"
              options={[
                { value: 'true', label: 'True' },
                { value: 'false', label: 'False' },
              ]}
              value={String(node.value === true)}
              onChange={(event) => onValue(event.target.value === 'true')}
              disabled={readOnly}
              className="w-28"
            />
          )}
        </div>
      );
    }
  }
}

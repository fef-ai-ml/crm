import type { ChangeEvent } from 'react';
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

export type AttributeLeafRowProps = {
  node: AttributeNode;
  path: NodePath;
  errors: FieldErrors;
  dispatch: (action: BuilderAction) => void;
  readOnly?: boolean;
};

export function AttributeLeafRow({ node, path, errors, dispatch, readOnly = false }: AttributeLeafRowProps) {
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
      </div>

      <div className="flex flex-col gap-1">
        <ValueControl node={node} readOnly={readOnly} onValue={handleValue} />
        {valueErrors.map((message) => (
          <p role="alert" key={message} className="text-xs text-red-600">
            {message}
          </p>
        ))}
      </div>
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
        <Input
          type="number"
          aria-label="Value"
          value={typeof node.value === 'number' ? node.value : ''}
          onChange={(event) => onValue(event.target.value === '' ? undefined : Number(event.target.value))}
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
            <Input
              type="number"
              aria-label="Value"
              value={typeof node.value === 'number' ? node.value : ''}
              onChange={(event) => onValue(event.target.value === '' ? 0 : Number(event.target.value))}
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

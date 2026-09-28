import type { ChangeEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Select, type SelectOption } from '@/components/ui/select';
import { AttributeLeafRow } from './AttributeLeafRow';
import { EventLeafRow } from './EventLeafRow';
import { defaultLeaf, errorsFor, testIdFor } from './reducer';
import type { BuilderAction, FieldErrors } from './reducer';
import type { GroupNode, GroupOp, RuleTree } from '@/rules/types';
import type { NodePath } from '@/rules/paths';

const GROUP_OPS: SelectOption[] = [
  { value: 'and', label: 'And' },
  { value: 'or', label: 'Or' },
  { value: 'not', label: 'Not' },
];

export type GroupRowProps = {
  node: GroupNode;
  path: NodePath;
  errors: FieldErrors;
  dispatch: (action: BuilderAction) => void;
  readOnly?: boolean;
  isRoot?: boolean;
};

export function GroupRow({ node, path, errors, dispatch, readOnly = false, isRoot = false }: GroupRowProps) {
  const groupErrors = errorsFor(errors, path);

  function handleOpChange(event: ChangeEvent<HTMLSelectElement>) {
    dispatch({ type: 'setGroupOp', path, op: event.target.value as GroupOp });
  }

  function addChild(kind: 'attribute' | 'event' | 'group') {
    const child: RuleTree = kind === 'group' ? { op: 'and', children: [defaultLeaf('attribute')] } : defaultLeaf(kind);
    dispatch({ type: 'addChild', path, node: child });
  }

  return (
    <div
      data-testid={`rule-node-${testIdFor(path)}`}
      className="flex flex-col gap-2 rounded-md border border-zinc-200 p-2 dark:border-zinc-800"
    >
      {groupErrors.map((message) => (
        <p role="alert" key={message} className="text-sm text-red-600">
          {message}
        </p>
      ))}

      <div className="flex flex-wrap items-center gap-2">
        <Select aria-label="Operator" options={GROUP_OPS} value={node.op} onChange={handleOpChange} disabled={readOnly} className="w-24" />

        {!readOnly && (
          <>
            <Button type="button" size="sm" variant="outline" onClick={() => addChild('attribute')}>
              Add condition
            </Button>
            <Button type="button" size="sm" variant="outline" onClick={() => addChild('event')}>
              Add event
            </Button>
            <Button type="button" size="sm" variant="outline" onClick={() => addChild('group')}>
              Add group
            </Button>
            {!isRoot && (
              <>
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
              </>
            )}
          </>
        )}
      </div>

      <div className="flex flex-col gap-2 border-l border-zinc-200 pl-3 dark:border-zinc-800">
        {node.children.map((child, index) => {
          const childPath = [...path, index];
          if ('op' in child) {
            return (
              <GroupRow key={index} node={child} path={childPath} errors={errors} dispatch={dispatch} readOnly={readOnly} />
            );
          }
          if (child.type === 'attribute') {
            return (
              <AttributeLeafRow
                key={index}
                node={child}
                path={childPath}
                errors={errors}
                dispatch={dispatch}
                readOnly={readOnly}
              />
            );
          }
          return (
            <EventLeafRow key={index} node={child} path={childPath} errors={errors} dispatch={dispatch} readOnly={readOnly} />
          );
        })}
      </div>
    </div>
  );
}

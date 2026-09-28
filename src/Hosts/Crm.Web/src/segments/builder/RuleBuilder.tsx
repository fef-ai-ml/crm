import { AttributeLeafRow } from './AttributeLeafRow';
import { EventLeafRow } from './EventLeafRow';
import { GroupRow } from './GroupRow';
import { builderReducer } from './reducer';
import type { BuilderAction, FieldErrors } from './reducer';
import type { RuleTree } from '@/rules/types';

export type RuleBuilderProps = {
  tree: RuleTree;
  onChange: (tree: RuleTree) => void;
  errors: FieldErrors;
  readOnly?: boolean;
};

/** The rule builder: a pure reducer over the API's own rule tree (J5), rendered as a tree of rows. This
 * component owns no state of its own — `tree` in, `onChange(builderReducer(tree, action))` out. */
export function RuleBuilder({ tree, onChange, errors, readOnly = false }: RuleBuilderProps) {
  function dispatch(action: BuilderAction) {
    onChange(builderReducer(tree, action));
  }

  if ('op' in tree) {
    return <GroupRow node={tree} path={[]} errors={errors} dispatch={dispatch} readOnly={readOnly} isRoot />;
  }
  if (tree.type === 'attribute') {
    return <AttributeLeafRow node={tree} path={[]} errors={errors} dispatch={dispatch} readOnly={readOnly} isRoot />;
  }
  return <EventLeafRow node={tree} path={[]} errors={errors} dispatch={dispatch} readOnly={readOnly} isRoot />;
}

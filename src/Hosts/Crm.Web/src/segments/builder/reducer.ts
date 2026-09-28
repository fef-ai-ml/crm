import { formatPath, type NodePath } from '@/rules/paths';
import type { AttributeNode, EventNode, GroupNode, GroupOp, RuleTree } from '@/rules/types';

/** The builder state IS the API's rule tree (J5): no mapping layer, no local UI state beyond it. Every action
 * addresses a node by its `NodePath` from the root, the same addressing `parseErrorPath`/`formatPath` use for
 * server and client validation errors, so one `FieldErrors` map serves both. */
export type BuilderAction =
  | { type: 'replace'; tree: RuleTree }
  | { type: 'setGroupOp'; path: NodePath; op: GroupOp }
  | { type: 'addChild'; path: NodePath; node: RuleTree } // append to the group at path
  | { type: 'removeNode'; path: NodePath } // path.length >= 1
  | { type: 'moveNode'; path: NodePath; direction: 'up' | 'down' } // no-op at the edges
  | { type: 'updateLeaf'; path: NodePath; leaf: AttributeNode | EventNode }
  | { type: 'wrapInGroup'; path: NodePath; op: GroupOp }; // node -> { op, children: [node] }

export function defaultLeaf(kind: 'attribute' | 'event'): AttributeNode | EventNode {
  return kind === 'attribute'
    ? { type: 'attribute', key: '', cmp: 'eq', value: '' }
    : { type: 'event', name: '', cmp: 'gte', count: 1, window: '30d' };
}

export function newDefinition(): GroupNode {
  return { op: 'and', children: [defaultLeaf('attribute')] };
}

/** Server field errors (the problem's `errors` map, J7) or client `validateRule` errors, both keyed by the
 * validator path (`formatPath`). Building this map from either source is the caller's job (Task 4); this
 * module only reads it. */
export type FieldErrors = ReadonlyMap<string, string[]>;

export function errorsFor(errors: FieldErrors, path: NodePath, field?: string): string[] {
  return errors.get(formatPath(path, field)) ?? [];
}

/** `data-testid="rule-node-<path>"` (J18): the root is `root`, `[2, 0]` is `2-0`. */
export function testIdFor(path: NodePath): string {
  return path.length === 0 ? 'root' : path.join('-');
}

/** Walks `path` from `tree`, rebuilding only the nodes on the way down (structural sharing): an untouched
 * sibling subtree keeps its object identity. Any path that runs off the tree (past a leaf, or an
 * out-of-range index) is a safe no-op returning `tree` unchanged. */
function updateAtPath(tree: RuleTree, path: NodePath, update: (node: RuleTree) => RuleTree): RuleTree {
  if (path.length === 0) return update(tree);
  if (!('op' in tree)) return tree;

  const [index, ...rest] = path;
  if (index < 0 || index >= tree.children.length) return tree;

  const children = tree.children.map((child, i) => (i === index ? updateAtPath(child, rest, update) : child));
  return { ...tree, children };
}

export function builderReducer(tree: RuleTree, action: BuilderAction): RuleTree {
  switch (action.type) {
    case 'replace':
      return action.tree;

    case 'setGroupOp':
      return updateAtPath(tree, action.path, (node) => (('op' in node) ? { ...node, op: action.op } : node));

    case 'addChild':
      return updateAtPath(tree, action.path, (node) =>
        'op' in node ? { ...node, children: [...node.children, action.node] } : node,
      );

    case 'updateLeaf':
      return updateAtPath(tree, action.path, () => action.leaf);

    case 'wrapInGroup':
      return updateAtPath(tree, action.path, (node) => ({ op: action.op, children: [node] }));

    case 'removeNode': {
      if (action.path.length === 0) return tree;
      const parentPath = action.path.slice(0, -1);
      const childIndex = action.path[action.path.length - 1];
      return updateAtPath(tree, parentPath, (node) => {
        if (!('op' in node)) return node;
        if (childIndex < 0 || childIndex >= node.children.length) return node;
        return { ...node, children: node.children.filter((_, i) => i !== childIndex) };
      });
    }

    case 'moveNode': {
      if (action.path.length === 0) return tree;
      const parentPath = action.path.slice(0, -1);
      const childIndex = action.path[action.path.length - 1];

      let parent: RuleTree | undefined = tree;
      for (const index of parentPath) {
        if (parent === undefined || !('op' in parent)) {
          parent = undefined;
          break;
        }
        parent = parent.children[index];
      }
      if (parent === undefined || !('op' in parent)) return tree;

      const targetIndex = action.direction === 'up' ? childIndex - 1 : childIndex + 1;
      if (targetIndex < 0 || targetIndex >= parent.children.length) return tree; // no-op at the edges

      return updateAtPath(tree, parentPath, (node) => {
        if (!('op' in node)) return node;
        const children = [...node.children];
        [children[childIndex], children[targetIndex]] = [children[targetIndex], children[childIndex]];
        return { ...node, children };
      });
    }

    default:
      return tree;
  }
}

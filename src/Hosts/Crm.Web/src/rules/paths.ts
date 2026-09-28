import type { RuleTree } from './types';

/** Indexes from the tree root down to a node, e.g. `[0, 2]` is `children[0].children[2]`. */
export type NodePath = number[];

const ERROR_PATH_RE = /^definition((?:\.children\[\d+\])*)(?:\.(\w+))?$/;
const CHILD_INDEX_RE = /\.children\[(\d+)\]/g;

/** The inverse of `formatPath`. `"definition"` → `{ indexes: [], field: null }`;
 * `"definition.children[1].key"` → `{ indexes: [1], field: 'key' }`; anything that isn't a `definition`-rooted
 * error path (as `validateRule` builds them) → `null`. */
export function parseErrorPath(path: string): { indexes: NodePath; field: string | null } | null {
  const match = ERROR_PATH_RE.exec(path);
  if (!match) return null;

  const childrenSegment = match[1] ?? '';
  const indexes: NodePath = [...childrenSegment.matchAll(CHILD_INDEX_RE)].map((m) => Number(m[1]));
  const field = match[2] ?? null;

  return { indexes, field };
}

/** The inverse of `parseErrorPath`. */
export function formatPath(indexes: NodePath, field?: string): string {
  const childrenPart = indexes.map((index) => `.children[${index}]`).join('');
  return field ? `definition${childrenPart}.${field}` : `definition${childrenPart}`;
}

/** Walks `children[i]` for each index in `path`, from the tree root. `[]` returns `tree` itself; an index past
 * a leaf (no `children`) or out of range returns `undefined`. */
export function nodeAt(tree: RuleTree, path: NodePath): RuleTree | undefined {
  let current: RuleTree | undefined = tree;
  for (const index of path) {
    if (current === undefined || !('op' in current)) return undefined;
    current = current.children[index];
  }
  return current;
}

import { z } from 'zod';
import type { RuleTree } from './types';
import { validateRule } from './validate';
import { parseErrorPath } from './paths';

/** `validateRule`'s error path, e.g. `"definition.children[0].window"`, as a Zod issue path: the root
 * `"definition"` prefix is dropped and each `.children[i]` becomes two path segments, `'children'` then `i` —
 * `"definition.children[0].window"` → `['children', 0, 'window']`. */
function toIssuePath(path: string): (string | number)[] {
  const parsed = parseErrorPath(path);
  if (!parsed) return [];

  const segments: (string | number)[] = [];
  for (const index of parsed.indexes) {
    segments.push('children', index);
  }
  if (parsed.field !== null) segments.push(parsed.field);

  return segments;
}

/** The Zod half of CT-002. This is not a tree of Zod primitives: `validateRule` IS the schema, re-emitted as
 * issues, because message/path/order parity with `RuleParser.cs` must be exact and independently maintaining
 * two validators would drift. */
export const ruleSchema: z.ZodType<RuleTree> = z.custom<RuleTree>(() => true).superRefine((value, ctx) => {
  for (const error of validateRule(value)) {
    ctx.addIssue({ code: 'custom', message: error.message, path: toIssuePath(error.path) });
  }
});

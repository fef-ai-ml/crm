/** The rule tree grammar (spec §5.1) and its limits, mirroring `RuleParser.cs`/`RuleLimits.cs` — the C# source
 * of truth (see `docs/test/architecture-contract.md` CT-002). No React, no I/O: pure model. */

export type GroupOp = 'and' | 'or' | 'not';

export type AttributeCmp =
  | 'eq'
  | 'neq'
  | 'in'
  | 'nin'
  | 'gt'
  | 'gte'
  | 'lt'
  | 'lte'
  | 'contains'
  | 'exists'
  | 'not_exists';

export type EventCmp = 'eq' | 'gte' | 'lte';

export type RuleValue = string | number | boolean | string[] | number[];

export type GroupNode = { op: GroupOp; children: RuleTree[] };

export type AttributeNode = { type: 'attribute'; key: string; cmp: AttributeCmp; value?: RuleValue };

export type EventNode = { type: 'event'; name: string; cmp: EventCmp; count: number; window: string };

export type RuleTree = GroupNode | AttributeNode | EventNode;

export type RuleError = { path: string; message: string };

/** Mirrors `RuleLimits.cs`, in one place, exactly as the C# side. */
export const RULE_LIMITS = {
  maxDepth: 5,
  maxLeaves: 20,
  maxStringLength: 256,
  maxListLength: 100,
  maxEventNameLength: 128,
  maxWindowDays: 365,
  maxWindowHours: 8760,
} as const;

// C# anchors these with `\z` (not `$`) because `$` also matches before a trailing newline there. JavaScript's
// `$` without the `m` flag already asserts the true end of the string with no such exception, so it is already
// equivalent to the C# `\z`-anchored pattern — no translation needed.
export const KEY_PATTERN = /^[A-Za-z0-9_]{1,64}$/;
export const WINDOW_PATTERN = /^([0-9]{1,4})(d|h)$/;

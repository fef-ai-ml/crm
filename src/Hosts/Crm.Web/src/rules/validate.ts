import { KEY_PATTERN, RULE_LIMITS, WINDOW_PATTERN } from './types';
import type { AttributeCmp, EventCmp, GroupOp, RuleError } from './types';

/** A structural stand-in for the parsed tree, just enough to compute depth/leaf counts the way `RuleParser.cs`'s
 * `DepthOf`/`CountLeaves` do over its `RuleNode` — `validateRule` reports only errors, never a tree, so the
 * walker below never needs to build the real `AttributeNode`/`EventNode` shapes. */
type ParsedNode = { kind: 'group'; children: ParsedNode[] } | { kind: 'leaf' };

const GROUP_OPS: readonly GroupOp[] = ['and', 'or', 'not'];
const ATTRIBUTE_CMPS: readonly AttributeCmp[] = [
  'eq',
  'neq',
  'in',
  'nin',
  'gt',
  'gte',
  'lt',
  'lte',
  'contains',
  'exists',
  'not_exists',
];
const EVENT_CMPS: readonly EventCmp[] = ['eq', 'gte', 'lte'];

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// C# checks `TryGetDouble`/`double.IsFinite` because a JSON number token with an extreme exponent (e.g.
// `1e400`) is syntactically valid but has no finite double value; a JS number can likewise be `Infinity`/`NaN`
// when a tree is hand-built rather than parsed from JSON text, so every "must be a number" check goes through
// this same finiteness test (per the controller resolution, not just the count check C# guards explicitly).
function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function getString(node: Record<string, unknown>, property: string): string | null {
  const value = node[property];
  return typeof value === 'string' ? value : null;
}

// A JSON `null` counts as "value present" — mirrors C#'s `TryGetProperty("value")` with kind != Undefined.
function hasOwnValue(node: Record<string, unknown>): boolean {
  return Object.hasOwn(node, 'value');
}

/** Mirrors `RuleParser.ParseNode`: errors are collected in pre-order, a group's own errors before its
 * children's, and a field error still yields a best-effort node so later siblings (and depth/leaf counting)
 * are unaffected — only a non-object node yields no node at all (mirrors `ParseNode`/`FailNode`). */
function parseNode(input: unknown, path: string, errors: RuleError[]): ParsedNode | null {
  if (!isPlainObject(input)) {
    errors.push({ path, message: 'unknown node type' });
    return null;
  }

  if (Object.hasOwn(input, 'op')) {
    return parseGroup(input, path, errors);
  }

  const type = getString(input, 'type');
  if (type === 'attribute') return parseAttribute(input, path, errors);
  if (type === 'event') return parseEvent(input, path, errors);

  errors.push({ path: `${path}.type`, message: 'unknown node type' });
  return { kind: 'leaf' };
}

function parseGroup(node: Record<string, unknown>, path: string, errors: RuleError[]): ParsedNode {
  const opRaw = node['op'];
  let op: GroupOp | null =
    typeof opRaw === 'string' && (GROUP_OPS as readonly string[]).includes(opRaw) ? (opRaw as GroupOp) : null;
  if (op === null) {
    errors.push({ path: `${path}.op`, message: 'unknown group operator' });
    op = 'and';
  }

  const childrenRaw = node['children'];
  const hasChildrenArray = Array.isArray(childrenRaw);
  const rawCount = hasChildrenArray ? childrenRaw.length : 0;

  // Node-level errors before children's, so pre-order puts a group's own arity error first (H19).
  if (op === 'not' && rawCount !== 1) {
    errors.push({ path, message: 'not must have exactly one child' });
  } else if (op !== 'not' && rawCount < 1) {
    errors.push({ path, message: 'group must have at least one child' });
  }

  const children: ParsedNode[] = [];
  if (hasChildrenArray) {
    childrenRaw.forEach((child, index) => {
      const parsed = parseNode(child, `${path}.children[${index}]`, errors);
      if (parsed !== null) children.push(parsed);
    });
  }

  return { kind: 'group', children };
}

function parseAttribute(node: Record<string, unknown>, path: string, errors: RuleError[]): ParsedNode {
  const key = getString(node, 'key') ?? '';
  if (!KEY_PATTERN.test(key)) {
    errors.push({ path: `${path}.key`, message: 'key must match ^[A-Za-z0-9_]{1,64}$' });
  }

  const cmpText = getString(node, 'cmp') ?? '';
  const cmp: AttributeCmp | null =
    (ATTRIBUTE_CMPS as readonly string[]).includes(cmpText) ? (cmpText as AttributeCmp) : null;
  if (cmp === null) {
    errors.push({ path: `${path}.cmp`, message: 'unknown comparator' });
  }

  parseAttributeValue(node, cmp, `${path}.value`, errors);
  return { kind: 'leaf' };
}

function parseAttributeValue(
  node: Record<string, unknown>,
  cmp: AttributeCmp | null,
  valuePath: string,
  errors: RuleError[],
): void {
  const hasValue = hasOwnValue(node);
  const value = node['value'];

  switch (cmp) {
    case 'exists':
    case 'not_exists':
      if (hasValue) errors.push({ path: valuePath, message: 'value is not allowed for this comparator' });
      return;

    case 'gt':
    case 'gte':
    case 'lt':
    case 'lte':
      if (!hasValue) {
        errors.push({ path: valuePath, message: 'value is required' });
        return;
      }
      if (!isFiniteNumber(value)) {
        errors.push({ path: valuePath, message: 'value must be a number' });
      }
      return;

    case 'contains':
      if (!hasValue) {
        errors.push({ path: valuePath, message: 'value is required' });
        return;
      }
      if (typeof value !== 'string') {
        errors.push({ path: valuePath, message: 'value must be a string' });
        return;
      }
      checkBoundedString(value, valuePath, errors);
      return;

    case 'in':
    case 'nin':
      if (!hasValue) {
        errors.push({ path: valuePath, message: 'value is required' });
        return;
      }
      checkListValue(value, valuePath, errors);
      return;

    case 'eq':
    case 'neq':
      if (!hasValue) {
        errors.push({ path: valuePath, message: 'value is required' });
        return;
      }
      checkScalarValue(value, valuePath, errors);
      return;

    default:
      // cmp is unknown (its own error was already recorded at `.cmp`); best-effort so a later sibling's own
      // errors are unaffected — mirrors the C# `default` arm of `ParseAttributeValue`.
      if (hasValue) checkScalarValue(value, valuePath, errors);
  }
}

function checkBoundedString(text: string, path: string, errors: RuleError[]): void {
  if (text.length > RULE_LIMITS.maxStringLength) {
    errors.push({ path, message: 'value must be at most 256 characters' });
  }
}

function checkListValue(value: unknown, path: string, errors: RuleError[]): void {
  const message = 'value must be a non-empty array of strings or of numbers (at most 100)';
  if (!Array.isArray(value) || value.length === 0 || value.length > RULE_LIMITS.maxListLength) {
    errors.push({ path, message });
    return;
  }

  const allStrings = value.every((item) => typeof item === 'string');
  const allNumbers = value.every(isFiniteNumber);
  if (!allStrings && !allNumbers) {
    errors.push({ path, message });
  }
}

function checkScalarValue(value: unknown, path: string, errors: RuleError[]): void {
  if (typeof value === 'string') {
    checkBoundedString(value, path, errors);
    return;
  }
  if (isFiniteNumber(value) || typeof value === 'boolean') {
    return;
  }
  errors.push({ path, message: 'value must be a string, number or boolean' });
}

function parseEvent(node: Record<string, unknown>, path: string, errors: RuleError[]): ParsedNode {
  const name = getString(node, 'name') ?? '';
  if (name.length === 0 || name.length > RULE_LIMITS.maxEventNameLength) {
    errors.push({ path: `${path}.name`, message: 'name is required' });
  }

  const cmpText = getString(node, 'cmp') ?? '';
  const cmp: EventCmp | null = (EVENT_CMPS as readonly string[]).includes(cmpText) ? (cmpText as EventCmp) : null;
  if (cmp === null) {
    errors.push({ path: `${path}.cmp`, message: 'unknown comparator' });
  }

  checkCount(node, path, errors);
  checkWindow(node, path, errors);

  return { kind: 'leaf' };
}

function checkCount(node: Record<string, unknown>, path: string, errors: RuleError[]): void {
  const count = node['count'];
  // A whole-number check, not an integer-parse: `3.0`/`3e0` are valid counts (H23), same as `RuleParser.ParseCount`.
  const isValid = isFiniteNumber(count) && Number.isInteger(count) && count >= 0 && count <= 2147483647;
  if (!isValid) {
    errors.push({ path: `${path}.count`, message: 'count must be at least 0' });
  }
}

function checkWindow(node: Record<string, unknown>, path: string, errors: RuleError[]): void {
  const message = 'window must match ^\\d+(d|h)$ and be at most 365 days';
  const text = getString(node, 'window') ?? '';
  const match = WINDOW_PATTERN.exec(text);
  if (!match) {
    errors.push({ path: `${path}.window`, message });
    return;
  }

  const amount = Number(match[1]);
  const unit = match[2];
  const max = unit === 'd' ? RULE_LIMITS.maxWindowDays : RULE_LIMITS.maxWindowHours;
  if (amount < 1 || amount > max) {
    errors.push({ path: `${path}.window`, message });
  }
}

function depthOf(node: ParsedNode): number {
  if (node.kind === 'group') {
    return node.children.length === 0 ? 1 : 1 + Math.max(...node.children.map(depthOf));
  }
  return 0;
}

function countLeaves(node: ParsedNode): number {
  if (node.kind === 'group') {
    return node.children.reduce((sum, child) => sum + countLeaves(child), 0);
  }
  return 1;
}

/** Mirrors `RuleParser.Parse` byte-for-byte: a pre-order walk of node errors, then (if the tree parsed) a depth
 * check, then a leaf-count check (H19). `[]` means valid. */
export function validateRule(input: unknown): RuleError[] {
  const errors: RuleError[] = [];
  const tree = parseNode(input, 'definition', errors);

  if (tree !== null) {
    const depth = depthOf(tree);
    if (depth > RULE_LIMITS.maxDepth) {
      errors.push({ path: 'definition', message: `depth must be at most ${RULE_LIMITS.maxDepth}` });
    }

    const leafCount = countLeaves(tree);
    if (leafCount > RULE_LIMITS.maxLeaves) {
      errors.push({ path: 'definition', message: `at most ${RULE_LIMITS.maxLeaves} leaves` });
    }
  }

  return errors;
}

export function firstErrorPath(input: unknown): string | null {
  const errors = validateRule(input);
  return errors.length > 0 ? errors[0].path : null;
}

import { isGroupNode } from './types';
import type { RuleTree } from './types';

/** Mirrors `RealtimeEligibility.Classify`/`FirstReason` (ADR-0005 §5.4): a rule tree can be evaluated in real
 * time from a profile snapshot plus event counters, with no need to know about absent data, when it has no
 * `not` group, every attribute leaf (any comparator) is fine, and every event leaf has `cmp` in `gte`/`eq` with
 * `count >= 1` for `gte` (an `eq` leaf is never real-time eligible, whatever its count; an `lte` leaf never is
 * either). Pre-order walk; the first reason found wins, but only the boolean matters here (J8). */
function isIneligible(node: RuleTree): boolean {
  if (isGroupNode(node)) {
    if (node.op === 'not') return true;
    return node.children.some(isIneligible);
  }

  if (node.type === 'event') {
    if (node.cmp === 'lte') return true;
    if (node.cmp === 'eq') return true; // count === 0 is ZeroCount, count >= 1 is ExactCount: both ineligible.
    return node.count === 0; // cmp === 'gte': ZeroCount only; count >= 1 is eligible.
  }

  return false; // an attribute leaf, any comparator, is always eligible.
}

export function isRealtimeEligible(tree: RuleTree): boolean {
  return !isIneligible(tree);
}

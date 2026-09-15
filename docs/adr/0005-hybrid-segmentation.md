# 0005. Evaluate segments with a hybrid of batch SQL and real-time stream processing

- **Status:** Accepted
- **Date:** 2026-09-15
- **Deciders:** project owner

## Context and Problem Statement

Marketers expect segment membership to reflect recent behavior quickly, but some rules (absence of an event, negations) can only be answered by looking at all data. How should the platform evaluate segment rules?

## Decision Drivers

- FR-15 and FR-16: batch materialization and real-time updates are both required.
- FR-13: comparators include zero counts and negation.
- NFR-02: real-time updates within 2 s.
- Simplicity: one rule format for both paths; no separate streaming query language.

## Considered Options

1. Hybrid: every segment compiles to SQL for batch runs; segments whose rules pass an eligibility classifier are also evaluated in real time against a profile snapshot plus Redis event counters, with batch runs correcting drift.
2. Batch only: a segment is a saved query executed on demand or on a schedule.
3. Real-time only: every event re-evaluates all affected segments with an indexed rule engine and full per-profile state in memory.

## Decision Outcome

Chosen option: "Hybrid", because it gives real-time behavior for the common case (positive attribute and "did at least N times in window" rules) while keeping correctness for rules that require global knowledge, and because the batch run doubles as a repair mechanism for the approximate real-time counters. Batch-only fails the real-time expectation; real-time-only requires per-profile state and rule indexing that are out of proportion for slice 1.

### Positive consequences

- One JSON rule tree serves both paths; the compiler and the evaluator share the same node types.
- The eligibility classifier is a pure function with unit tests; the UI badge makes the behavior visible to users.
- Replaying `events.processed` rebuilds real-time state without touching ingestion.

### Negative consequences

- Real-time event counters use a TTL window, not a precise sliding window; membership may be slightly stale until the next batch run.
- Two code paths to keep consistent; golden-file tests for the compiler and property-style tests for the evaluator are required.

## Consequences

Eligibility rule: no `not` group, and every event leaf uses `gte` with count ≥ 1 (spec §5.4). Batch runs rebuild the Redis set for eligible segments (spec §5.3 step 5). Membership storage follows [0008](0008-run-and-swap-membership.md).

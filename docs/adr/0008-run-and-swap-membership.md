# 0008. Materialize segment membership as immutable runs with run-and-swap

- **Status:** Accepted
- **Date:** 2026-09-15
- **Deciders:** project owner

## Context and Problem Statement

A batch segment evaluation may take minutes on large tenants. Readers must never see a half-written membership list, and later features (campaign triggers, journeys) need to know who entered or exited a segment and when. How is batch membership stored?

## Decision Drivers

- FR-15: atomic swap, entered/exited history, keep last 3 runs.
- FR-18: members and changes are pageable over the API.
- Roadmap items 2 and 3 consume membership changes as triggers.
- Auditability: the SQL that produced a run should be inspectable.

## Considered Options

1. Run-and-swap: each run writes into its own `SegmentRunMembers` partition keyed by `RunId`; a diff against the previous run yields `MembershipChanges`; the segment's `CurrentRunId` pointer is swapped in one update; old runs pruned.
2. In-place update of a single `SegmentMembers` table with insert/delete per profile.
3. No materialization; every read re-executes the compiled SQL.

## Decision Outcome

Chosen option: "Run-and-swap", because writers never touch rows that readers are using, the swap is a single-row update, the diff between two complete sets is a trivial `EXCEPT` in both directions, and keeping the compiled SQL on the run row gives an audit trail. In-place updates require row-level locking or accept torn reads. No materialization makes member listing and counts expensive and offers no change history.

### Positive consequences

- Readers always see a complete, consistent membership.
- `MembershipChanges` is exactly the event stream a campaign trigger needs.
- A failed run leaves the previous run current; nothing to roll back.

### Negative consequences

- Storage is up to three full membership copies per segment; pruning is mandatory.
- Real-time membership (Redis sets) and batch membership (SQL runs) are two representations; the batch run rebuilds the Redis set to reconcile them.

## Consequences

Tables `SegmentRuns`, `SegmentRunMembers`, `MembershipChanges` are defined in the [segments data dictionary](../data-dictionary/segments.md). Batch procedure is spec §5.3. Related: [0005](0005-hybrid-segmentation.md).

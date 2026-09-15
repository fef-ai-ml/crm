# 0009. Allow Segmentation to read profile tables directly via SQL

- **Status:** Accepted
- **Date:** 2026-09-15
- **Deciders:** project owner

## Context and Problem Statement

[0001](0001-modular-monolith.md) forbids a module from referencing another module's assemblies or tables. Segment rules, however, are predicates over profiles and events, which belong to the Profiles module. Strict adherence would require Segmentation to keep its own copy of every profile and event. How should Segmentation access the data it filters?

## Decision Drivers

- FR-14: compiled rules are parameterized T-SQL over profile and event rows.
- NFR-08: module boundaries enforced by tests; exceptions must be explicit.
- YAGNI: do not replicate 100k+ event rows per tenant for a single consumer.

## Considered Options

1. Documented exception: Segmentation.Infrastructure executes raw SQL against `profiles.Profiles` and `profiles.Events` (read-only), with no reference to Profiles assemblies; the architecture test allows this specific schema read.
2. Data replication: Segmentation consumes `events.processed` and maintains its own `segments.ProfileSnapshots` and `segments.EventLog` tables.
3. Query API: Profiles module exposes an in-process query service that Segmentation calls with a compiled predicate object.

## Decision Outcome

Chosen option: "Documented exception with raw read-only SQL", because the rule compiler's natural output is SQL over those tables, replication doubles storage and introduces lag between truth and evaluation, and a query API would either leak SQL fragments across the boundary or reimplement the compiler inside Profiles. The coupling is to two table shapes, recorded here and in the data dictionary; the assembly boundary remains intact.

### Positive consequences

- Zero replication; batch runs see exactly the data the Profiles module wrote.
- Assembly-level isolation still holds; extraction to a service later means replacing this adapter with replication (option 2), which the `events.processed` topic already supports.

### Negative consequences

- A column rename in `profiles.*` breaks Segmentation SQL at runtime, not compile time. Mitigation: integration tests run every golden-file rule against the real schema.
- Segmentation must inject the tenant predicate itself ([0004](0004-shared-db-tenancy.md)); the compiler does this at the root unconditionally.

## Consequences

The only tables Segmentation may read outside its schema are `profiles.Profiles` and `profiles.Events`, read-only. `Crm.ArchTests` still forbids the assembly reference. This covers batch runs, preview counts, and the members listing. Any additional cross-schema read requires a new record.

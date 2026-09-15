# 0003. Store profiles as core columns plus JSON attributes in SQL Server

- **Status:** Accepted
- **Date:** 2026-09-15
- **Deciders:** project owner

## Context and Problem Statement

Profiles carry a small fixed set of fields (ids, email, phone, timestamps) and an open-ended set of tenant-defined attributes. Segment rules filter on both. Events carry open-ended properties. How are profiles and events stored so that the rule compiler emits readable SQL and the data stays in one system of record?

## Decision Drivers

- FR-12 to FR-14: rule trees compile to parameterized T-SQL that is easy to read and test.
- FR-10: attribute updates are key-by-key, last write wins.
- Single source of truth (see [0006](0006-redis-roles.md)).
- SQL Server 2022 JSON functions (`JSON_VALUE`, `JSON_MODIFY`, `ISJSON`, `OPENJSON`).

## Considered Options

1. Core columns plus one `Attributes NVARCHAR(MAX)` JSON column; persisted computed columns with indexes for hot keys (`plan`, `country`, `city` in slice 1).
2. Entity-attribute-value table `ProfileAttributes(ProfileId, Key, StringValue, NumberValue, DateValue)` with an index per value column.
3. Profiles held in Redis hashes as the primary store; SQL Server holds only events and segment membership.

## Decision Outcome

Chosen option: "Core columns plus JSON attributes", because an attribute predicate compiles to a single `JSON_VALUE(p.Attributes, '$.key') = @p` expression (or a promoted column), event predicates stay `EXISTS` subqueries against one events table, and everything remains in SQL Server. EAV would turn every leaf into a self-join and make compiled SQL unreadable. Redis-primary splits the truth and forces batch jobs to read from a cache.

### Positive consequences

- Compiled SQL is short enough for golden-file tests to double as documentation.
- `JSON_MODIFY` per key implements last-write-wins without a read-modify-write of the whole object.
- Hot keys get real indexes via computed columns without a schema change to the JSON.

### Negative consequences

- Non-promoted attribute predicates scan the tenant's profiles; acceptable at portfolio scale and documented in spec §11.
- The list of promoted keys is fixed in slice 1; a per-tenant promotion feature is a later slice.

## Consequences

Tables, indexes, and the monthly partitioning of `profiles.Events` are in the [profiles data dictionary](../data-dictionary/profiles.md). The rule compiler's output shape is fixed by this decision (spec §5.2).

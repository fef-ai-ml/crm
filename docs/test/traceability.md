# Traceability Matrix

Every requirement in the [BRD](../brd/BRD.md) maps to at least one test case; every test case defined in [unit.md](unit.md), [integration.md](integration.md), [architecture-contract.md](architecture-contract.md), and [e2e-load.md](e2e-load.md) appears here. `tools/docs_check.py` enforces both directions.

## Functional Requirements

| Requirement | Summary | Cases |
|---|---|---|
| FR-01 | track payload shape | UT-001, IT-001 |
| FR-02 | identify payload shape | UT-005, IT-002 |
| FR-03 | batch of ≤ 500 with per-item results | UT-006, UT-056, IT-003 |
| FR-04 | validation limits (name, size, timestamp) | UT-002, UT-003, UT-004 |
| FR-05 | UUIDv7 id, Kafka key, 202 without DB | UT-007, UT-008, IT-001, IT-039, CT-004 |
| FR-06 | duplicate event ids ignored | IT-004 |
| FR-07 | DLQ after three retries | IT-005 |
| FR-08 | identity kinds map to one profile | UT-009, IT-007 |
| FR-09 | merge rules, locks, re-routing, EventCount | UT-010, UT-011, UT-012, UT-013, IT-007, IT-008, IT-009, IT-010 |
| FR-10 | event vs identify updates | UT-014, UT-015, IT-002 |
| FR-11 | profile search and timeline | IT-011, IT-012 |
| FR-12 | rule tree shape and limits | UT-016, UT-017, UT-018, CT-002 |
| FR-13 | comparators and windows | UT-019, UT-020, UT-021, UT-022, UT-023, UT-024, UT-025, UT-026, UT-027, UT-028, UT-029, UT-030, UT-031, UT-032, UT-033, UT-034, UT-036, UT-038, CT-002 |
| FR-14 | parameterized SQL only | UT-020, UT-035, UT-037, UT-039, IT-042 |
| FR-15 | batch run, diff, swap, prune, schedule | IT-016, IT-017, IT-018, IT-019, IT-020, IT-021, E2E-004 |
| FR-16 | real-time eligibility and evaluation | UT-040, UT-041, UT-042, UT-043, UT-044, UT-045, UT-046, UT-047, UT-048, UT-049, IT-022, IT-023, IT-024, IT-025, IT-026, IT-027, IT-043 |
| FR-17 | preview count, timeout, cache | UT-058, IT-014, IT-015 |
| FR-18 | segment CRUD, runs, members, changes | IT-013, IT-028, IT-029 |
| FR-19 | login, roles | UT-053, UT-059, IT-036, IT-037, E2E-001, E2E-008 |
| FR-20 | profile explorer UI | E2E-006 |
| FR-21 | segment builder UI | UT-056, E2E-002, E2E-003, E2E-004 |
| FR-22 | segment list columns and badge | IT-028, E2E-005 |
| FR-23 | API key lifecycle | UT-050, UT-051, UT-052, IT-033, IT-034, E2E-007 |
| FR-24 | tenant scoping, fail closed | UT-054, UT-055, UT-057, IT-030, IT-031, IT-032, AT-006 |
| FR-25 | development-only tenant bootstrap | IT-038 |

## Non-Functional Requirements

| Requirement | Summary | Cases |
|---|---|---|
| NFR-01 | 1,000 events/s for 60 s | LT-001 |
| NFR-02 | latency budgets | IT-001, IT-022, LT-001, LT-002, LT-003 |
| NFR-03 | tenant isolation in data access | UT-057, IT-030, IT-031, AT-006 |
| NFR-04 | durability: acks=all, commit after SQL | IT-006, IT-039 |
| NFR-05 | effectively-once processing | IT-004 |
| NFR-06 | traces, logs, metrics | IT-040 |
| NFR-07 | hashed keys, JWT lifetime, rate limit | UT-050, UT-051, UT-053, IT-033, IT-035, IT-036, LT-004 |
| NFR-08 | module boundaries enforced | AT-001, AT-002, AT-003, AT-004, AT-005, AT-007 |
| NFR-09 | contract stability | CT-001, CT-003 |
| NFR-10 | runs with docker compose | IT-041, CT-005, LT-005 |

## Coverage summary

| Level | Cases |
|---|---|
| Unit (UT) | 58 |
| Integration (IT) | 43 |
| Architecture (AT) | 7 |
| Contract (CT) | 5 |
| End-to-end (E2E) | 8 |
| Load (LT) | 5 |
| Total | 126 |

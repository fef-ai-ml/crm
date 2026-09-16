# Test Specification

Test cases for slice 1, written before implementation. Every case traces to a requirement in the [BRD](../brd/BRD.md) and follows the levels defined in the [design spec §8.3](../superpowers/specs/2026-09-15-crm-slice1-design.md). Implementation plans turn these cases into executable tests first, then write the code that makes them pass.

## Levels and Tools

| Level | Prefix | Tool | Runs where | Document |
|---|---|---|---|---|
| Unit | `UT` | xUnit + FluentAssertions; golden files for the rule compiler | every build, no containers | [unit.md](unit.md) |
| Integration | `IT` | xUnit + Testcontainers (SQL Server 2022, Kafka KRaft, Redis 7), WebApplicationFactory for the API | CI and local, ~5 min | [integration.md](integration.md) |
| Architecture | `AT` | NetArchTest | every build | [architecture-contract.md](architecture-contract.md) |
| Contract | `CT` | build steps: OpenAPI drift, rule-fixture parity, docs checker, compose validation | every build | [architecture-contract.md](architecture-contract.md) |
| End-to-end | `E2E` | Playwright against `docker compose` | nightly and before merge to main | [e2e-load.md](e2e-load.md) |
| Load | `LT` | k6 against `docker compose` | on demand; results recorded in README | [e2e-load.md](e2e-load.md) |

Shared inputs (seed dataset, rule fixtures, golden SQL) are defined in [fixtures.md](fixtures.md). The requirement → case matrix is [traceability.md](traceability.md).

## Case Identifiers

`<PREFIX>-<nnn>`, three digits, unique within the whole set, never reused after deletion. Ids appear as level-3 headings `### UT-001 — title`; `tools/docs_check.py` parses these headings and fails when a case is defined but not traced, traced but not defined, or duplicated.

## Case Format

```
### IT-004 — duplicate event id is ignored
- Traces: FR-06, NFR-05
- Given a stored event (Id = X, OccurredAt = T)
- When the same Kafka message is redelivered
- Then the Events row count is unchanged, EventCount is unchanged, and the offset is committed
- Planned: tests/Crm.IntegrationTests/Ingestion/IdempotencyTests.cs
```

Rules: one observable behaviour per case; Given/When/Then name concrete values where the spec fixes them (limits, TTLs, status codes); "Planned" names the test file the implementation plan must create. A case that needs seed data names the fixture from [fixtures.md](fixtures.md).

## Running

Target commands once the implementation lands (they are the contract the implementation plans must satisfy):

```bash
dotnet test --filter Category=Unit                 # UT + AT, seconds
dotnet test --filter Category=Integration          # IT, needs Docker
dotnet test                                        # all .NET levels
python3 tools/docs_check.py                        # CT-003
cd src/Hosts/Crm.Web && npm test                   # rule-builder reducer, Zod fixture parity (CT-002)
cd src/Hosts/Crm.Web && npx playwright test        # E2E, needs `make up` + `make seed`
k6 run tests/Crm.LoadTests/ingest.js               # LT-001..LT-003
```

## Definition of Done

Slice 1 is done when:

1. Every case in this specification exists as an executable test or a documented manual step (LT-005 only) and passes.
2. `traceability.md` shows at least one passing case for every FR-01…FR-25 and NFR-01…NFR-10.
3. Load results for LT-001…LT-003 are recorded in the root README with the machine profile they were measured on.
4. `python3 tools/docs_check.py` is green and CI runs UT, AT, CT, and IT on every pull request.

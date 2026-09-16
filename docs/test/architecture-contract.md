# Architecture and Contract Test Cases

Architecture cases (`AT`) run with NetArchTest against the compiled solution on every build and encode the dependency rules of the [design spec §2.4](../superpowers/specs/2026-09-15-crm-slice1-design.md) and [ADR 0009](../adr/0009-segmentation-reads-profiles-tables.md). Contract cases (`CT`) are build steps that fail CI when a generated artifact or a shared fixture set drifts.

## Architecture

### AT-001 — modules depend only on Kernel and Contracts
- Traces: NFR-08
- Given assemblies `Ingestion.*`, `Profiles.*`, `Segmentation.*`, `Tenancy.*`
- When NetArchTest evaluates `ShouldNot().HaveDependencyOnAny(<other module namespaces>)`
- Then every module assembly passes; the only allowed cross-module references are `Crm.Kernel` and `Crm.Contracts`
- Planned: tests/Crm.ArchTests/ModuleBoundaryTests.cs

### AT-002 — Segmentation does not reference Profiles assemblies
- Traces: NFR-08
- Given `Segmentation.Domain`, `Segmentation.Application`, `Segmentation.Infrastructure`
- When dependencies on `Profiles.*` are checked
- Then none exist, even though Segmentation reads `profiles.*` tables by SQL (ADR 0009)
- Planned: tests/Crm.ArchTests/ModuleBoundaryTests.cs

### AT-003 — Ingestion has no database dependency
- Traces: NFR-08
- Given `Ingestion.Application` and `Ingestion.Infrastructure`
- When dependencies on `Microsoft.EntityFrameworkCore`, `Microsoft.Data.SqlClient`, `StackExchange.Redis` are checked
- Then none exist; the only infrastructure dependency is `Confluent.Kafka`
- Planned: tests/Crm.ArchTests/ModuleBoundaryTests.cs

### AT-004 — no module references a host
- Traces: NFR-08
- Given all module assemblies
- When dependencies on `Crm.Api` and `Crm.Worker` are checked
- Then none exist
- Planned: tests/Crm.ArchTests/ModuleBoundaryTests.cs

### AT-005 — hosts reference only module Infrastructure projects
- Traces: NFR-08
- Given `Crm.Api` and `Crm.Worker`
- When their module references are enumerated
- Then each is a `*.Infrastructure` assembly (for DI registration) and no host type calls a `*.Application` or `*.Domain` type directly except through registered interfaces
- Planned: tests/Crm.ArchTests/HostBoundaryTests.cs

### AT-006 — every entity is tenant-scoped
- Traces: FR-24, NFR-03
- Given every EF Core entity type in every module DbContext except `identity.Tenants` and the ASP.NET Identity tables
- When the model is inspected
- Then each has a `TenantId` property and a global query filter referencing `ITenantContext`, and the save interceptor is registered on every DbContext
- Planned: tests/Crm.ArchTests/TenantScopingTests.cs

### AT-007 — Segmentation SQL touches only the sanctioned profile tables
- Traces: NFR-08
- Given all string constants and golden files in `Segmentation.Infrastructure` and `tests/fixtures/golden/`
- When they are scanned for `profiles.` table references and for `INSERT`, `UPDATE`, `DELETE`, `MERGE` against them
- Then the only referenced tables are `profiles.Profiles` and `profiles.Events` and no write statement targets a `profiles.` table
- Planned: tests/Crm.ArchTests/CrossSchemaReadTests.cs

## Contract

### CT-001 — OpenAPI document has no drift
- Traces: NFR-09
- Given `docs/api/openapi.json` committed and the API project built
- When `dotnet run --project src/Hosts/Crm.Api -- --export-openapi build/openapi.json` runs in CI
- Then `diff docs/api/openapi.json build/openapi.json` is empty; the exported document lists exactly the endpoints in [api/README.md](../api/README.md)
- Planned: .github/workflows/ci.yml step `openapi-drift`

### CT-002 — rule fixtures agree between C# and Zod
- Traces: FR-12, FR-13
- Given `tests/fixtures/rules/valid/*.json` and `tests/fixtures/rules/invalid/*.json`
- When the C# `RuleValidator` (xUnit theory) and the Zod schema (Vitest) each run over both directories
- Then every `valid` fixture passes both and every `invalid` fixture fails both, with the same first error path
- Planned: tests/Segmentation.UnitTests/RuleFixtureParityTests.cs and src/Hosts/Crm.Web/src/rules/schema.test.ts

### CT-003 — documentation checker is green
- Traces: NFR-09
- Given the repository at the commit under test
- When `python3 tools/docs_check.py` runs in CI
- Then it exits 0 and prints `0 errors`; the manifest count matches the number of documents listed in [docs/README.md](../README.md) plus the test specification
- Planned: .github/workflows/ci.yml step `docs-check`

### CT-004 — Kafka message contracts are snapshot-tested
- Traces: FR-05
- Given `TrackEventRaw`, `IdentifyRaw`, `EventTracked`, `ProfileUpdated`, `ProfileMerged` in `Crm.Contracts`
- When each is serialized with the production JSON options and compared to `tests/fixtures/contracts/<type>.json`
- Then field names are camelCase and match the contract table in [redis-and-kafka.md](../data-dictionary/redis-and-kafka.md); any change requires updating the snapshot deliberately
- Planned: tests/Crm.Contracts.UnitTests/ContractSnapshotTests.cs

### CT-005 — compose configuration is valid and exposes the documented ports
- Traces: NFR-10
- Given `deploy/docker-compose.yml` and `deploy/docker-compose.observability.yml`
- When `docker compose -f … config` runs in CI, then its JSON output is piped into `tools/compose_check.py`
- Then both render without error, services `sqlserver`, `kafka`, `kafka-init`, `redis`, `api`, `worker`, `web` exist, `web` publishes 3000, `api` publishes 8080, `worker` publishes 8081, `kafka` publishes 19092, `sqlserver`/`redis` publish their configurable host port onto container ports 1433/6379, every service but `web` has a healthcheck (web's readiness comes from its image), and `worker`'s `depends_on` requires `sqlserver`/`redis` healthy and `kafka-init` completed
- Implemented: `tools/compose_check.py`, `make compose-check`, `.github/workflows/ci.yml` step `validate compose files`. `deploy/docker-compose.observability.yml` is added in plan C and checked by the same step once it exists.

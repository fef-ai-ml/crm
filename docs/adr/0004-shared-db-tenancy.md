# 0004. Use a shared database with a TenantId column for multi-tenancy

- **Status:** Accepted
- **Date:** 2026-09-15
- **Deciders:** project owner

## Context and Problem Statement

Every tenant (customer workspace) must be fully isolated from every other tenant, while the platform runs as one deployment on one developer machine. Which isolation model balances safety, cost, and realism?

## Decision Drivers

- NFR-03: isolation enforced in the data access layer and tested.
- FR-24: a request without a resolvable tenant fails rather than returning unscoped data.
- NFR-10: single `docker compose` environment; no per-tenant provisioning.
- Portfolio value: shared-schema multi-tenancy is the common SaaS pattern and shows query-filter discipline.

## Considered Options

1. Shared database and schema; every table has `TenantId`; EF Core global query filters and a save interceptor enforce scope.
2. Database per tenant with a connection-string catalog.
3. Schema per tenant within one database.

## Decision Outcome

Chosen option: "Shared database with TenantId column", because it needs no provisioning, keeps migrations to one set, and isolation is enforced centrally: a global query filter on every entity, an interceptor that stamps and verifies `TenantId` on writes, and a middleware that throws when no tenant is resolved. Database-per-tenant gives stronger isolation but is disproportionate for a portfolio project; schema-per-tenant complicates migrations for little gain.

### Positive consequences

- One migration set, one connection, one backup.
- Isolation is testable: an integration test creates two tenants and proves cross-reads fail at the API and query level.
- Redis keys and Kafka messages carry the tenant id, so the same discipline extends beyond SQL.

### Negative consequences

- A bug that bypasses the query filter (raw SQL without the tenant predicate) could leak data; the rule compiler therefore always injects `p.TenantId = @tenant` at the root.
- Noisy-neighbor effects across tenants are not mitigated in slice 1.

## Consequences

`ITenantContext` in `Crm.Kernel` is the only source of the current tenant. `TenantContextMissingException` is a 500 and logged as a bug. Every Redis key is prefixed `t:{tenantId}:` (see [0006](0006-redis-roles.md)). Column definitions are in the [data dictionary](../data-dictionary/README.md).

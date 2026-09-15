# Architecture Decision Records

Decisions that shape the CRM platform, recorded in [MADR](https://adr.github.io/madr/) format. Each record captures the context, the options considered, the choice, and its consequences at the time it was made. Records are immutable; a changed decision gets a new record that supersedes the old one.

## Index

| ID | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith.md) | Modular monolith over microservices | Accepted |
| [0002](0002-kafka-ingestion.md) | Kafka for event ingestion | Accepted |
| [0003](0003-sql-server-json-profiles.md) | Profiles as core columns plus JSON attributes in SQL Server | Accepted |
| [0004](0004-shared-db-tenancy.md) | Shared database with TenantId column for multi-tenancy | Accepted |
| [0005](0005-hybrid-segmentation.md) | Hybrid batch and real-time segmentation | Accepted |
| [0006](0006-redis-roles.md) | Redis as a rebuildable cache, never a source of truth | Accepted |
| [0007](0007-auth-model.md) | ASP.NET Identity JWT for users, hashed API keys for ingest | Accepted |
| [0008](0008-run-and-swap-membership.md) | Immutable segment runs with run-and-swap membership | Accepted |
| [0009](0009-segmentation-reads-profiles-tables.md) | Segmentation reads profile tables directly via SQL | Accepted |

## Adding an ADR

1. Copy [template.md](template.md) to `NNNN-kebab-slug.md` using the next number.
2. Fill every section; list at least two considered options.
3. Add a row to the index above.
4. Add the file to the `ADRS` list in `tools/docs_check.py`, then run `python3 tools/docs_check.py --only docs/adr`.

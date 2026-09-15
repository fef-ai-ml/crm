# Data Dictionary

Canonical names and shapes for every persistent store in slice 1. EF Core entities, migrations, Redis key builders, and Kafka topic constants must match these documents exactly; a change here is a change to the contract.

## Conventions

- Primary keys are `uniqueidentifier` generated as UUIDv7 (`Guid.CreateVersion7()`), so they sort by creation time.
- Every table has `TenantId uniqueidentifier NOT NULL`, `CreatedAt datetime2(3) NOT NULL`, `UpdatedAt datetime2(3) NOT NULL` (UTC). These three columns are omitted from the per-table lists below.
- Every EF Core entity has a global query filter on `TenantId` ([ADR 0004](../adr/0004-shared-db-tenancy.md)).
- JSON columns are `nvarchar(max)` with a `CHECK (ISJSON(col) = 1)` constraint.
- Timestamps are `datetime2(3)` in UTC.
- Index names: `IX_<Table>_<Columns>`; unique: `UX_<Table>_<Columns>`; primary key: `PK_<Table>`.
- Each module owns one schema and one migration history table `__EFMigrationsHistory_<schema>`.

## Schemas

| Schema | Owner module | Document |
|---|---|---|
| `profiles` | Profiles | [profiles.md](profiles.md) |
| `segments` | Segmentation | [segments.md](segments.md) |
| `identity` | Tenancy | [identity.md](identity.md) |
| Redis and Kafka | Profiles, Segmentation, Tenancy | [redis-and-kafka.md](redis-and-kafka.md) |

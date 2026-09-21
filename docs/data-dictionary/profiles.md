# Schema `profiles`

Owner: Profiles module. Read by Segmentation via raw read-only SQL ([ADR 0009](../adr/0009-segmentation-reads-profiles-tables.md)). Storage rationale: [ADR 0003](../adr/0003-sql-server-json-profiles.md).

## Profiles

One row per unified end user of a tenant.

| Column | Type | Null | Description |
|---|---|---|---|
| Id | uniqueidentifier | no | Primary key, UUIDv7 |
| ExternalId | nvarchar(128) | yes | Customer's own user id |
| Email | nvarchar(256) | yes | Normalized lower-case, trimmed |
| Phone | nvarchar(32) | yes | E.164 format |
| Attributes | nvarchar(max) JSON | no | Object of tenant-defined attributes; default `{}` |
| FirstSeenAt | datetime2(3) | no | First event or identify received |
| LastSeenAt | datetime2(3) | no | Latest event received |
| EventCount | bigint | no | Maintained by the worker; default 0 |
| MergedIntoId | uniqueidentifier | yes | Set on merge losers; such rows are excluded from all reads |
| Attr_plan | nvarchar(256) computed persisted | yes | `JSON_VALUE(Attributes, '$.plan')` |
| Attr_country | nvarchar(256) computed persisted | yes | `JSON_VALUE(Attributes, '$.country')` |
| Attr_city | nvarchar(256) computed persisted | yes | `JSON_VALUE(Attributes, '$.city')` |

Indexes:

| Name | Columns | Notes |
|---|---|---|
| PK_Profiles | Id | nonclustered |
| CX_Profiles_TenantId_Id | TenantId, Id | clustered |
| UX_Profiles_TenantId_ExternalId | TenantId, ExternalId | unique, filtered `WHERE ExternalId IS NOT NULL` |
| UX_Profiles_TenantId_Email | TenantId, Email | unique, filtered `WHERE Email IS NOT NULL` |
| UX_Profiles_TenantId_Phone | TenantId, Phone | unique, filtered `WHERE Phone IS NOT NULL` |
| IX_Profiles_TenantId_LastSeenAt_Id | TenantId, LastSeenAt DESC, Id DESC; INCLUDE ExternalId, Email, Phone, EventCount, MergedIntoId; WHERE MergedIntoId IS NULL | keyset index of GET /v1/profiles: browse and search pages are an ordered scan, no sort, no lookups. `MergedIntoId` is INCLUDEd (not selected) because SQL Server only elides the filter's re-check against the base table when the filtered column is itself present in the index |
| IX_Profiles_TenantId_Attr_plan | TenantId, Attr_plan | promoted attribute |
| IX_Profiles_TenantId_Attr_country | TenantId, Attr_country | promoted attribute |
| IX_Profiles_TenantId_Attr_city | TenantId, Attr_city | promoted attribute |

Constraints: `CK_Profiles_Attributes_IsJson CHECK (ISJSON(Attributes) = 1)`; `FK_Profiles_MergedIntoId` references `Profiles(Id)`, created without a supporting index — merge losers are never looked up by it.

## UserIdentities

Maps a (kind, value) identity to a profile. Exactly one profile per identity per tenant.

| Column | Type | Null | Description |
|---|---|---|---|
| Kind | varchar(16) | no | One of `external_id`, `anonymous_id`, `email`, `phone` |
| Value | nvarchar(256) | no | Normalized identity value |
| ProfileId | uniqueidentifier | no | FK to Profiles(Id) |

Indexes:

| Name | Columns | Notes |
|---|---|---|
| PK_UserIdentities | TenantId, Kind, Value | clustered |
| IX_UserIdentities_ProfileId | ProfileId | re-pointing on merge |

Constraint: `CK_UserIdentities_Kind CHECK (Kind IN ('external_id','anonymous_id','email','phone'))`. The default database collation makes `Value` case-insensitive in SQL, while the Redis identity-cache key (`t:{tenantId}:id:{kind}:{value}`) is case-sensitive: a differently-cased id is a cache miss that still resolves to the same profile once SQL is consulted.

## Events

Append-only timeline. One row per `track` event; `identify` calls never write here. Partitioned monthly on `OccurredAt` via partition function `PF_Events_Monthly` and scheme `PS_Events_Monthly`; `PF_Events_Monthly` ships with static boundaries 2026-01-01 … 2027-12-01, and `EventPartitionMaintenanceService` keeps the current month plus the next two present (on start-up, then every 24 h).

| Column | Type | Null | Description |
|---|---|---|---|
| Id | uniqueidentifier | no | UUIDv7 assigned by the API; idempotency key |
| ProfileId | uniqueidentifier | no | FK to Profiles(Id); re-pointed on merge |
| Name | nvarchar(64) | no | `[A-Za-z0-9_]{1,64}` |
| Properties | nvarchar(max) JSON | no | Default `{}`; ≤ 32 KB enforced at the API |
| OccurredAt | datetime2(3) | no | Client timestamp, truncated to milliseconds and clamped to ReceivedAt ± 7 d before storage |
| ReceivedAt | datetime2(3) | no | Stamped by the API |

Indexes:

| Name | Columns | Notes |
|---|---|---|
| CX_Events_TenantId_ProfileId_OccurredAt_Id | TenantId, ProfileId, OccurredAt, Id | unique, clustered, partition-aligned; the timeline's total order (read backwards) |
| UX_Events_Id | Id, OccurredAt | unique on (Id, OccurredAt); this table has no `PRIMARY KEY` constraint, so on a partitioned table this is the key EF uses for the enforceable idempotency check. Kafka redelivery carries identical Id and OccurredAt, so duplicates are rejected. |
| IX_Events_TenantId_Name_OccurredAt | TenantId, Name, OccurredAt | includes ProfileId; segment event predicates |

`LOCK_ESCALATION = AUTO` — locks escalate to the month partition, not the table.

Constraint: `CK_Events_Properties_IsJson CHECK (ISJSON(Properties) = 1)`; `FK_Events_ProfileId` references `Profiles(Id)`, created without a supporting index of its own. Retention: none in slice 1 (partitions allow cheap future purge).

## Read paths

Search (`GET /v1/profiles`) matches by prefix `LIKE` on the three contact columns (`ExternalId`, `Email`, `Phone`, relying on the database's default collation for case-insensitivity); pages are read in index order from `IX_Profiles_TenantId_LastSeenAt_Id`, the cursor seeks to its position, `q` is a residual predicate on that scan (a rare match on a very large tenant scans until a page is full — known limitation, ruling G4); the plan is asserted by `ReadPathPlanTests`. The timeline (`GET /v1/profiles/{id}/events`) seeks `CX_Events_TenantId_ProfileId_OccurredAt_Id` for the profile's range; within a single month partition the total-order key needs no sort, measured with a window inside the current month; across partitions (the unrestricted second page) a Sort still appears over the per-partition seeks (`Top > Sort > Clustered Index Seek`) — the optimizer does not treat partition order as `OccurredAt` order end to end. The `name` filter stays a residual predicate on the profile's range (ruling G8). Asserted by `ReadPathPlanTests`. Every profile read filters `MergedIntoId IS NULL` itself.

`JSON_VALUE` returns text for every JSON scalar (numbers included), so any numeric predicate over an `Attributes` value — e.g. `ltv` — must `TRY_CONVERT(decimal(18, 2), JSON_VALUE(Attributes, '$.ltv'))` before comparing; this is binding for the Segmentation rule compiler (IT-014).

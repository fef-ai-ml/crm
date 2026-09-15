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
| IX_Profiles_TenantId_LastSeenAt | TenantId, LastSeenAt DESC | profile explorer default sort |
| IX_Profiles_TenantId_Attr_plan | TenantId, Attr_plan | promoted attribute |
| IX_Profiles_TenantId_Attr_country | TenantId, Attr_country | promoted attribute |
| IX_Profiles_TenantId_Attr_city | TenantId, Attr_city | promoted attribute |

Constraints: `CK_Profiles_Attributes_IsJson CHECK (ISJSON(Attributes) = 1)`; `FK_Profiles_MergedIntoId` references `Profiles(Id)`.

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

Constraint: `CK_UserIdentities_Kind CHECK (Kind IN ('external_id','anonymous_id','email','phone'))`.

## Events

Append-only timeline. Partitioned monthly on `OccurredAt` via partition function `PF_Events_Monthly` and scheme `PS_Events_Monthly`; a maintenance job adds the next month's boundary on the first day of each month.

| Column | Type | Null | Description |
|---|---|---|---|
| Id | uniqueidentifier | no | UUIDv7 assigned by the API; idempotency key |
| ProfileId | uniqueidentifier | no | FK to Profiles(Id); re-pointed on merge |
| Name | nvarchar(64) | no | `[A-Za-z0-9_]{1,64}` |
| Properties | nvarchar(max) JSON | no | Default `{}`; ≤ 32 KB enforced at the API |
| OccurredAt | datetime2(3) | no | Client timestamp clamped to ReceivedAt ± 7 d |
| ReceivedAt | datetime2(3) | no | Stamped by the API |

Indexes:

| Name | Columns | Notes |
|---|---|---|
| CX_Events_TenantId_ProfileId_OccurredAt | TenantId, ProfileId, OccurredAt | clustered, partition-aligned |
| UX_Events_Id | Id, OccurredAt | unique; `OccurredAt` included for partition alignment |
| IX_Events_TenantId_Name_OccurredAt | TenantId, Name, OccurredAt | includes ProfileId; segment event predicates |

Constraint: `CK_Events_Properties_IsJson CHECK (ISJSON(Properties) = 1)`. Retention: none in slice 1 (partitions allow cheap future purge).

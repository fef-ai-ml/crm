# Schema `segments`

Owner: Segmentation module. Membership model rationale: [ADR 0008](../adr/0008-run-and-swap-membership.md); evaluation model: [ADR 0005](../adr/0005-hybrid-segmentation.md).

All four tables derive from `Crm.Kernel.Entity` (H9): `TenantId`, `CreatedAt`, `UpdatedAt` are present on every table except where a table's ruling says otherwise (below). No table in this schema has a foreign key into `profiles` or `identity` — `ProfileId` columns are plain `uniqueidentifier`, validated by the run query, not by the database (module boundary, ADR-0009).

## Segments

| Column | Type | Null | Description |
|---|---|---|---|
| Id | uniqueidentifier | no | Primary key |
| Name | nvarchar(128) | no | Unique per tenant |
| Description | nvarchar(1024) | yes | |
| Definition | nvarchar(max) JSON | no | Canonical rule tree (`CanonicalRuleWriter.Write`), spec §5.1 |
| DefinitionVersion | int | no | Rule format version; 1 in slice 1 |
| DefinitionHash | binary(32) | no | SHA-256 of the canonical Definition; used to detect unchanged definitions on save and shared with the preview cache key function |
| IsRealtimeEligible | bit | no | Result of the eligibility classifier at save time |
| Status | varchar(16) | no | `Draft`, `Active`, `Archived` |
| CurrentRunId | uniqueidentifier | yes | FK to SegmentRuns(RunId), NoAction; null until first successful run |
| MemberCount | int | no | Copied from the current run; default 0 |
| LastRunAt | datetime2(3) | yes | Finish time of the current run |
| TenantId | uniqueidentifier | no | |
| CreatedAt | datetime2(3) | no | |
| UpdatedAt | datetime2(3) | no | |

Indexes: `PK_Segments (Id)` nonclustered; `CX_Segments_TenantId_Id (TenantId, Id)` unique clustered; `UX_Segments_TenantId_Name (TenantId, Name)` unique; `IX_Segments_TenantId_CreatedAt_Id (TenantId, CreatedAt DESC, Id DESC)` (H11 keyset index) `INCLUDE (Name, Status, MemberCount, LastRunAt, IsRealtimeEligible)`; `IX_Segments_CurrentRunId` — EF's automatic index backing `FK_Segments_CurrentRunId` (CurrentRunId is not a leading key column of any other index on this table).

Constraints: `CK_Segments_Definition_IsJson CHECK (ISJSON([Definition]) = 1)`; `CK_Segments_Status CHECK (Status IN ('Draft','Active','Archived'))`; `FK_Segments_CurrentRunId (CurrentRunId) REFERENCES SegmentRuns(RunId) ON DELETE NO ACTION`.

`Segments.CurrentRunId` and `SegmentRuns.SegmentId` form a cycle; the migration creates both tables, then adds `FK_SegmentRuns_SegmentId` (the second FK) once both exist.

## SegmentRuns

| Column | Type | Null | Description |
|---|---|---|---|
| RunId | uniqueidentifier | no | Primary key; maps `Entity.Id` to column `RunId` (H9) |
| SegmentId | uniqueidentifier | no | FK to Segments(Id), NoAction |
| Status | varchar(16) | no | `Queued`, `Running`, `Succeeded`, `Failed` |
| StartedAt | datetime2(3) | yes | |
| FinishedAt | datetime2(3) | yes | The run's finish time; batch-written MembershipChanges rows use this as `OccurredAt` (H7) |
| MemberCount | int | yes | Set on Succeeded |
| CompiledSql | nvarchar(max) | yes | Audit copy of the executed SQL (parameters listed as names) |
| Error | nvarchar(4000) | yes | Set on Failed |
| Trigger | varchar(16) | no | `Manual` or `Schedule` |
| TenantId | uniqueidentifier | no | |
| CreatedAt | datetime2(3) | no | |
| UpdatedAt | datetime2(3) | no | |

Indexes: `PK_SegmentRuns (RunId)` clustered; `IX_SegmentRuns_TenantId_SegmentId_CreatedAt_RunId (TenantId, SegmentId, CreatedAt DESC, RunId DESC)` (H11 keyset index); `IX_SegmentRuns_SegmentId` — EF's automatic index backing `FK_SegmentRuns_SegmentId` (SegmentId is only the second key column of the composite index above, so it does not already cover the FK).

Constraints: `CK_SegmentRuns_Status CHECK (Status IN ('Queued','Running','Succeeded','Failed'))`; `CK_SegmentRuns_Trigger CHECK (Trigger IN ('Manual','Schedule'))`; `FK_SegmentRuns_SegmentId (SegmentId) REFERENCES Segments(Id) ON DELETE NO ACTION`.

## SegmentRunMembers

`Entity.Id`, `CreatedAt` and `UpdatedAt` are not mapped (H9) — the table is exactly the three columns below.

| Column | Type | Null | Description |
|---|---|---|---|
| TenantId | uniqueidentifier | no | |
| RunId | uniqueidentifier | no | FK to SegmentRuns(RunId), cascade delete |
| ProfileId | uniqueidentifier | no | No FK to profiles schema (cross-module); validated by the run query |

Indexes: `PK_SegmentRunMembers (RunId, ProfileId)` clustered; `IX_SegmentRunMembers_TenantId_ProfileId (TenantId, ProfileId)` for "which segments contain this profile".

Constraints: `FK_SegmentRunMembers_RunId (RunId) REFERENCES SegmentRuns(RunId) ON DELETE CASCADE`.

Pruning: runs older than the newest 3 per segment are deleted; cascade removes their members.

## MembershipChanges

`CreatedAt` and `UpdatedAt` are not mapped (H9) — `OccurredAt` is the only timestamp. The first run of a segment records every member as `Entered` (H7); batch writers insert with SQL `NEWID()` for `Id` and the run's finish time for `OccurredAt`, rather than constructing `MembershipChange.Record` (which is for application-side, non-batch writes and produces a UUIDv7 id via `Entity.Id`'s default).

| Column | Type | Null | Description |
|---|---|---|---|
| Id | uniqueidentifier | no | Primary key, UUIDv7 (application writes) or `NEWID()` (batch writes) |
| SegmentId | uniqueidentifier | no | No FK to Segments: batch writes insert many rows per run and a segment is never deleted (`Segment.Archive` keeps the row), so the constraint would only cost write throughput |
| ProfileId | uniqueidentifier | no | |
| Change | varchar(8) | no | `Entered` or `Exited` |
| Source | varchar(8) | no | `Batch` or `Realtime` |
| OccurredAt | datetime2(3) | no | |
| TenantId | uniqueidentifier | no | |

Indexes: `PK_MembershipChanges (Id)` nonclustered; `CX_MembershipChanges_TenantId_SegmentId_OccurredAt_Id (TenantId, SegmentId, OccurredAt DESC, Id DESC)` unique clustered (H11); `IX_MembershipChanges_TenantId_ProfileId (TenantId, ProfileId)`.

Constraints: `CK_MembershipChanges_Change CHECK (Change IN ('Entered','Exited'))`; `CK_MembershipChanges_Source CHECK (Source IN ('Batch','Realtime'))`. Retention: none in slice 1.

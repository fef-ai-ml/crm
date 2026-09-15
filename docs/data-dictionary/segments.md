# Schema `segments`

Owner: Segmentation module. Membership model rationale: [ADR 0008](../adr/0008-run-and-swap-membership.md); evaluation model: [ADR 0005](../adr/0005-hybrid-segmentation.md).

## Segments

| Column | Type | Null | Description |
|---|---|---|---|
| Id | uniqueidentifier | no | Primary key |
| Name | nvarchar(128) | no | Unique per tenant |
| Description | nvarchar(1024) | yes | |
| Definition | nvarchar(max) JSON | no | Rule tree, spec §5.1 |
| DefinitionVersion | int | no | Rule format version; 1 in slice 1 |
| DefinitionHash | binary(32) | no | SHA-256 of the canonical Definition; used to detect unchanged definitions on save and shared with the preview cache key function |
| IsRealtimeEligible | bit | no | Result of the eligibility classifier at save time |
| Status | varchar(16) | no | `Draft`, `Active`, `Archived` |
| CurrentRunId | uniqueidentifier | yes | FK to SegmentRuns(RunId); null until first successful run |
| MemberCount | int | no | Copied from the current run; default 0 |
| LastRunAt | datetime2(3) | yes | Finish time of the current run |

Indexes: `PK_Segments (Id)`; `CX_Segments_TenantId_Id (TenantId, Id)` clustered; `UX_Segments_TenantId_Name (TenantId, Name)` unique; `IX_Segments_TenantId_Status (TenantId, Status)`.

Constraints: `CK_Segments_Definition_IsJson`; `CK_Segments_Status CHECK (Status IN ('Draft','Active','Archived'))`.

## SegmentRuns

| Column | Type | Null | Description |
|---|---|---|---|
| RunId | uniqueidentifier | no | Primary key |
| SegmentId | uniqueidentifier | no | FK to Segments(Id) |
| Status | varchar(16) | no | `Queued`, `Running`, `Finished`, `Failed`. Queued rows are picked up by the Worker poll (5 s). |
| StartedAt | datetime2(3) | yes | |
| FinishedAt | datetime2(3) | yes | |
| MemberCount | int | yes | Set on Finished |
| CompiledSql | nvarchar(max) | yes | Audit copy of the executed SQL (parameters listed as names) |
| Error | nvarchar(4000) | yes | Set on Failed |
| Trigger | varchar(16) | no | `Manual` or `Schedule` |

Indexes: `PK_SegmentRuns (RunId)`; `IX_SegmentRuns_TenantId_SegmentId_StartedAt (TenantId, SegmentId, StartedAt DESC)`.

## SegmentRunMembers

| Column | Type | Null | Description |
|---|---|---|---|
| RunId | uniqueidentifier | no | FK to SegmentRuns(RunId), cascade delete |
| ProfileId | uniqueidentifier | no | No FK to profiles schema (cross-module); validated by the run query |

Indexes: `PK_SegmentRunMembers (RunId, ProfileId)` clustered; `IX_SegmentRunMembers_TenantId_ProfileId (TenantId, ProfileId)` for "which segments contain this profile".

Pruning: runs older than the newest 3 per segment are deleted; cascade removes their members.

## MembershipChanges

| Column | Type | Null | Description |
|---|---|---|---|
| Id | uniqueidentifier | no | Primary key, UUIDv7 |
| SegmentId | uniqueidentifier | no | FK to Segments(Id) |
| ProfileId | uniqueidentifier | no | |
| Change | varchar(8) | no | `Entered` or `Exited` |
| Source | varchar(8) | no | `Batch` or `Realtime` |
| OccurredAt | datetime2(3) | no | |

Indexes: `PK_MembershipChanges (Id)`; `CX_MembershipChanges_TenantId_SegmentId_OccurredAt (TenantId, SegmentId, OccurredAt)` clustered; `IX_MembershipChanges_TenantId_ProfileId (TenantId, ProfileId)`.

Constraints: `CK_MembershipChanges_Change CHECK (Change IN ('Entered','Exited'))`; `CK_MembershipChanges_Source CHECK (Source IN ('Batch','Realtime'))`. Retention: none in slice 1.

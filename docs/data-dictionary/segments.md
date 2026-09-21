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
| Status | varchar(16) | no | `Queued`, `Running`, `Finished`, `Failed` |
| StartedAt | datetime2(3) | yes | |
| FinishedAt | datetime2(3) | yes | The run's finish time; batch-written MembershipChanges rows use this as `OccurredAt` (H7) |
| MemberCount | int | yes | Set on Finished |
| CompiledSql | nvarchar(max) | yes | Audit copy of the executed SQL (parameters listed as names) |
| Error | nvarchar(4000) | yes | Set on Failed |
| Trigger | varchar(16) | no | `Manual` or `Schedule` |
| TenantId | uniqueidentifier | no | |
| CreatedAt | datetime2(3) | no | |
| UpdatedAt | datetime2(3) | no | |

Indexes: `PK_SegmentRuns (RunId)` clustered; `IX_SegmentRuns_TenantId_SegmentId_CreatedAt_RunId (TenantId, SegmentId, CreatedAt DESC, RunId DESC)` (H11 keyset index); `IX_SegmentRuns_SegmentId` — EF's automatic index backing `FK_SegmentRuns_SegmentId` (SegmentId is only the second key column of the composite index above, so it does not already cover the FK); `UX_SegmentRuns_TenantId_SegmentId_Active (TenantId, SegmentId) UNIQUE WHERE Status IN ('Queued','Running')` (fix round 1, H5) — the database, not the application's check-then-insert, is what enforces at most one active run per segment; `SegmentRunQueue.EnqueueAsync` catches the resulting unique-violation and returns the winning run instead of a 500.

Constraints: `CK_SegmentRuns_Status CHECK (Status IN ('Queued','Running','Finished','Failed'))`; `CK_SegmentRuns_Trigger CHECK (Trigger IN ('Manual','Schedule'))`; `FK_SegmentRuns_SegmentId (SegmentId) REFERENCES Segments(Id) ON DELETE NO ACTION`.

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
| SegmentId | uniqueidentifier | no | FK to Segments(Id), NoAction |
| ProfileId | uniqueidentifier | no | |
| Change | varchar(8) | no | `Entered` or `Exited` |
| Source | varchar(8) | no | `Batch` or `Realtime` |
| OccurredAt | datetime2(3) | no | |
| TenantId | uniqueidentifier | no | |

Indexes: `PK_MembershipChanges (Id)` nonclustered; `CX_MembershipChanges_TenantId_SegmentId_OccurredAt_Id (TenantId, SegmentId, OccurredAt DESC, Id DESC)` unique clustered (H11); `IX_MembershipChanges_TenantId_ProfileId (TenantId, ProfileId)`; `IX_MembershipChanges_SegmentId` — EF's automatic index backing `FK_MembershipChanges_SegmentId` (SegmentId is only the second key column of the clustered index above, so it does not already cover the FK).

Constraints: `CK_MembershipChanges_Change CHECK (Change IN ('Entered','Exited'))`; `CK_MembershipChanges_Source CHECK (Source IN ('Batch','Realtime'))`; `FK_MembershipChanges_SegmentId (SegmentId) REFERENCES Segments(Id) ON DELETE NO ACTION` (segments are archived, never deleted, so this reference is always safe). Retention: none in slice 1.

## Read paths

`GET /segments/{id}/members` (H13/H28) is raw SQL — parameters only, no `TagWith`, so its first line is the literal comment `-- segments-members`, which is how the plan test finds it in the plan cache — reading `segments.SegmentRunMembers` joined to `profiles.Profiles` for the segment's `CurrentRunId`; a segment with no finished run is an empty page, not a 404. The brief's single-statement cursor predicate, `(@after IS NULL OR m.ProfileId < @after)`, measured with no seek on `ProfileId` at all: SQL Server 2022 instead ranges `IX_SegmentRunMembers_TenantId_ProfileId` on `TenantId` alone (RunId and the cursor become residual filters), avoiding a `Sort` only because that index happens to already be ordered by `ProfileId`. Splitting into two constant statements — one without the cursor predicate, one with a hard `m.ProfileId < @after` — was **not enough on its own**: SQL Server still preferred that same nonclustered index even with the hard `<`, because it is keyed `(TenantId, ProfileId)` across every run of every segment the tenant has ever had — its `RunId` residual is not bounded by pruning's "at most 4 runs per segment" retention, it grows with the tenant's *segment count* (200 segments × 4 runs ≈ 800 candidate rows per returned row in the worst case; H28, a genuine plan defect in H11, not a wording issue). Both statements now hint `WITH (FORCESEEK (PK_SegmentRunMembers (RunId)))` (first page) / `WITH (FORCESEEK (PK_SegmentRunMembers (RunId, ProfileId)))` (cursor page), pinning the read to a seek straight to the run and a backward range on `ProfileId` — O(page), not O(the tenant's total membership rows). Measured with the hint: both pages show no `Sort`; indexes are exactly `PK_SegmentRunMembers` plus `CX_Profiles_TenantId_Id` (for the join to `profiles.Profiles`); seek columns are `RunId` (first page) and `RunId, ProfileId` (cursor page); operators `Top > Nested Loops > Clustered Index Seek > Clustered Index Seek` (a `Merge Join` sometimes appears in place of `Nested Loops`, cardinality-dependent — not assertion-relevant). `IX_SegmentRunMembers_TenantId_ProfileId` is no longer an accepted plan for this read; it exists for the tenant-scoped "which segments contain this profile" lookup instead (its own documented purpose in the SegmentRunMembers table above), not for a single run's member page. Mutation-checked: removing the cursor page's `FORCESEEK` hint reddens the plan test back to the nonclustered index (fix-round-1 report has the evidence, including a data-shape pitfall: a second overlapping segment/run initially tried to make the defect more visible instead made the optimizer prefer the good plan even without the hint on this tiny dataset, masking the very regression the mutation check exists to catch — dropped in favor of the single-run arrangement, which does discriminate).

`GET /segments/{id}/changes` (IT-029) is EF LINQ, tagged `segments-changes`, keyset-paged by `(OccurredAt, Id)` descending with the conjunctive cursor form (ruling G3); an optional `since` (`DateTimeOffset`, invariant culture, `AssumeUniversal | AdjustToUniversal`) adds `OccurredAt >= since`, an unparsable value is `400 segments.invalid_filter`. Measured cursor-page plan: `Top > Clustered Index Seek` on `CX_MembershipChanges_TenantId_SegmentId_OccurredAt_Id` alone, no `Sort`, seek columns `TenantId, SegmentId, OccurredAt` — the clustered key is already the read's total order, so a page costs one ranged seek. Both plan assertions are mutation-checked (task-8 and fix-round-1 reports): ordering the members cursor page by `p.LastSeenAt` instead of `m.ProfileId`, dropping the members cursor page's `FORCESEEK` hint, or ordering the changes page by `Id` alone instead of `(OccurredAt DESC, Id DESC)`, each turn the corresponding `HasSort`/`Indexes`/`SeekColumns` assertion red. Both reads are tenant-isolated the same way segment detail is (`segment.not_found` for an unknown or another tenant's id) — the raw members SQL also carries `m.TenantId = @tenant` itself, since it bypasses the EF global filter entirely. Asserted by `SegmentReadPlanTests`; case coverage in `SegmentReadTests`.

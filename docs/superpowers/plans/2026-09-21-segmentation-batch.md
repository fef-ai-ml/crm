# Segmentation — rules, batch materialization and API (plan H) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A tenant can define a segment as a rule tree, preview its size, run it in batch against profiles and events, and read its members, runs and membership changes through `/v1/segments…` — with the rule compiler proven by golden files and by execution against the seed dataset.

**Architecture:** `Segmentation.Domain` holds the rule model, parser/validator, canonical form, hash and real-time eligibility classifier (pure). `Segmentation.Application` holds the `RuleCompiler` (pure: tree + tenant + clock → parameterised SQL over `profiles.Profiles`/`profiles.Events`, ADR-0009) and the use-case contracts. `Segmentation.Infrastructure` holds the `segments` schema (EF Core), the raw-SQL executor for compiled queries, the endpoints, and the Worker's run poller + 6-hour scheduler. Real-time evaluation (the `events.processed` consumer, Redis sets and counters) is **plan I** and is not built here.

**Tech Stack:** .NET 10, EF Core 10 (SQL Server 2022), StackExchange.Redis (preview cache only), xUnit + FluentAssertions 7, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-15-crm-slice1-design.md` (§3.2, §5, §6, ADR-0005, ADR-0009). Test specification: `docs/test/{unit,integration,architecture-contract,fixtures}.md`. Compiler mapping table: `docs/design/architecture.md` §6.

## Global Constraints

- Work on `main`. Commit trailer exactly `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Never bypass commit signing; on a signing failure stage, `git write-tree`, report `DONE_UNCOMMITTED`. `git add` explicit paths only; never add `mise.toml`, `.superpowers/`, `.env.local`.
- Forbidden: reading/editing `deploy/.env`, `.env.local`, `~/.config`, `~/.local`; `podman machine`, `docker context …`, `make up*`, `make down`, `make tunnel*`, `make seed`, `make migrate`, `pkill`, prune, `coldstart.sh`; any statement against the compose stack's database; `git stash/restore/reset/checkout --/clean`; `curl`/`wget`; dispatching subagents.
- Every dotnet command: foreground, one at a time, prefixed `DOTNET_SYSTEM_NET_DISABLEIPV6=1`. Never `rm -rf obj bin`. Integration tests need `DOCKER_HOST=unix://$HOME/.local/share/containers/home-lab.sock TESTCONTAINERS_HOST_OVERRIDE=home-lab TESTCONTAINERS_RYUK_DISABLED=true MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest`. Logs go to the absolute workspace path given in the dispatch, never to `./logs`.
- Warnings are errors (CS1573, CA1873, CA2100, CA2000 bite). No `.ConfigureAwait(false)`. Test files need `using Xunit;`. FluentAssertions stays on 7.x.
- Module boundary (AT-001/002/004): `Segmentation.*` references only `Crm.Kernel`, `Crm.Contracts` and itself — never `Profiles.*`, `Tenancy.*`, `Ingestion.*` or a host. It reads `profiles.Profiles` and `profiles.Events` by raw SQL only, and writes nothing in `profiles` (ADR-0009, AT-007).
- Handlers return `IResult` or a Kernel `Error` (rendered by `ErrorResultFilter`). The host applies auth policies and tags; modules never name Tenancy's constants.
- Lists: `Crm.Kernel.Paging` (`KeysetCursor`, `PageLimit`, `PagingErrors`), fetch `limit + 1`, conjunctive cursor predicate, a query tag, index containing every evaluated column (spec "Rulings (plan G)").
- No user-supplied value ever appears in SQL text — only in parameters. Attribute keys reach SQL text only after matching `^[A-Za-z0-9_]{1,64}$`.
- `docs/api/openapi.json` is regenerated with `make openapi` in every task that changes the HTTP surface; `python3 tools/docs_check.py` stays at 0 errors.

## Rulings (H1–H22)

| # | Ruling | Why | Cost if wrong |
|---|---|---|---|
| H1 | Segmentation is two plans: H (this) = rules, schema, CRUD, preview, batch runs, reads, seed segments; I = real-time (consumer, `RealtimeEvaluator` UT-046…049, Redis sets/counters, cache invalidation, Redis rebuild after a run, IT-022…028, LT-003, `crm_realtime_eval_seconds`). | Same split as Profiles E/F; each half ships working software. | None — plan I builds on H's tables. |
| H2 | In plan H, `realtimeMemberCount` is always `null` in the detail response; `IsRealtimeEligible` is computed and stored at save time. | The field belongs to the contract now so OpenAPI does not change twice. | Plan I fills it. |
| H3 | No Quartz.NET. One `BackgroundService` (`SegmentRunWorker`) polls every 5 s and drains all queued runs sequentially; a second timer in the same service fires every `Segmentation:Schedule:Interval` (default `06:00:00`) and queues a `Schedule` run for every Active segment (all tenants) that has no Queued/Running run. The spec sentence is amended. | The repo's precedent for timed work is a `BackgroundService` (`EventPartitionMaintenanceService`); Quartz adds a dependency and a job store for two timers. IT-021 calls the scheduler method directly instead of advancing a Quartz clock. | Swap is contained in one class. Schedule phase resets on Worker restart (documented). |
| H4 | A run is claimed atomically: `UPDATE … SET Status='Running', StartedAt=@now … WHERE RunId=@id AND Status='Queued'`; zero rows = someone else has it. `Running` runs older than 10 minutes are marked `Failed` ("abandoned") at the start of each poll. | Two Worker replicas must not execute one run twice; a crashed Worker must not leave a segment blocked. | 10 min > the 5 min command timeout, so a live run is never reaped. |
| H5 | `POST /segments/{id}/run` while the segment already has a Queued or Running run returns `202` with **that** run's id; on an Archived segment it returns `409 segment.archived`; Draft segments can be run manually. | "One run per segment at a time"; idempotent button. | — |
| H6 | The whole run (insert members → diff → swap → finish → prune) is ONE SQL transaction with command timeout 300 s (`Segmentation:Run:CommandTimeoutSeconds`). On any exception: rollback, then mark the run `Failed` with `Error` = first 4,000 chars of the message in a separate transaction; the segment keeps its current run. | Failure cleanup is the rollback; readers only ever follow `CurrentRunId`, so a half-written run is unreachable (IT-018). | A very large run holds log space for its duration. |
| H7 | First run of a segment records every member as `Entered`. Batch change rows get `Id = NEWID()` (set-based insert) and `OccurredAt` = the run's finish time. | `MembershipChanges` is the future Journeys trigger feed; a first run is an entry. | Data dictionary says UUIDv7 — amended: v7 for rows written by code, `NEWID()` for set-based batch rows; `Id` is only a tiebreak. |
| H8 | Prune keeps the newest 3 terminal runs (Finished/Failed) per segment by `CreatedAt DESC, RunId DESC`, never deletes the `CurrentRunId` run, never touches Queued/Running. | IT-019; a current run must survive three later failures. | — |
| H9 | All four entities derive from `Crm.Kernel.Entity` (AT-006 then covers them). `SegmentRun.Id` maps to column `RunId`. `SegmentRunMember` ignores `Id`, `CreatedAt`, `UpdatedAt` (key `(RunId, ProfileId)`, precedent: `UserIdentity`); `MembershipChange` ignores `CreatedAt`, `UpdatedAt`. | Precedent; keeps the two high-volume tables as narrow as the data dictionary says. | — |
| H10 | Sort keys: segments list `CreatedAt DESC, Id DESC`; runs `CreatedAt DESC, RunId DESC` (`StartedAt` is null while Queued); changes `OccurredAt DESC, Id DESC`; members `ProfileId DESC` with `KeysetCursor(DateTime.UnixEpoch, profileId)` — members have no time dimension, the timestamp slot is a constant. Cursor scopes: `segments`, `segment-runs`, `segment-members`, `segment-changes`. | `KeysetCursor` is reused unchanged (ruling F2). | A one-field cursor type would be cleaner; not worth a Kernel type for one endpoint. |
| H11 | Indexes (replace the data dictionary's pre-G shapes): `IX_Segments_TenantId_CreatedAt_Id (TenantId, CreatedAt DESC, Id DESC) INCLUDE (Name, Status, MemberCount, LastRunAt, IsRealtimeEligible)` replaces `IX_Segments_TenantId_Status`; `IX_SegmentRuns_TenantId_SegmentId_CreatedAt_RunId (TenantId, SegmentId, CreatedAt DESC, RunId DESC)` replaces the `StartedAt` one; `CX_MembershipChanges_TenantId_SegmentId_OccurredAt_Id` unique clustered `(TenantId, SegmentId, OccurredAt DESC, Id DESC)`; `PK_SegmentRunMembers (RunId, ProfileId)` clustered stays (RunId is globally unique; `TenantId` is in the row, so the residual tenant check needs no lookup). | Spec "Rulings (plan G)". | — |
| H12 | Plan-shape tests (cached plan, `SeekColumns`, mutation-checked) cover **members** and **changes** only. Segments and runs lists use the pattern but are not plan-asserted: runs are pruned to ~3 per segment and segments are tens per tenant — the optimizer legitimately prefers a scan on a handful of rows. | A plan assertion on a 3-row table pins noise. | — |
| H13 | The members query is raw SQL (it joins `profiles.Profiles`, which Segmentation has no entity for); it carries the comment `-- segments-members` as its first line so `QueryPlans.LatestAsync` finds it. Inner join, no `MergedIntoId` filter: a member merged since the run stays listed until the next run. | ADR-0009. | Count on the page and `memberCount` agree. |
| H14 | Segment list with no `status` parameter returns Draft + Active; `status=Archived` (or `Draft`/`Active`) filters exactly; any other value → `400 segments.invalid_filter`. `status` is a residual predicate on the ordered index (as `q` in G4). | DELETE is an archive; an archived segment in the default list is surprising. | One query parameter. |
| H15 | Create defaults to `Draft`; create accepts `Draft`/`Active`; PUT accepts all three (un-archive allowed); DELETE sets `Archived` → 204, idempotent. Name: trimmed, 1–128; description ≤ 1,024; unique per tenant including archived → `409 segment.name_taken` (pre-check + catch of SQL error 2601/2627). Foreign, missing → one `404 segment.not_found`. | IT-013; ruling F8's single 404. | — |
| H16 | `@since` is a **parameter** computed from the injected `IClock` at compile time, not `SYSUTCDATETIME()` in SQL text (UT-029 pins `@p1 = 2026-08-17T00:00:00Z`). `architecture.md` §6 is corrected. Event leaf parameter order: name, since, count (`eq 0` emits no count). | The golden tests are the contract; deterministic. | Compile and execute are milliseconds apart. |
| H17 | Value typing. `gt/gte/lt/lte` require a number. `eq/neq/in/nin` with number value(s) compile to `TRY_CONVERT(float, JV) <op> @p` (float parameters) — comparing the text column to a float parameter would raise a conversion error on the first non-numeric row. Strings → `nvarchar` parameters; booleans → the strings `true`/`false` (UT-038). `contains` requires a string. `in/nin` require a non-empty array (≤ 100) of all strings or all numbers. `exists/not_exists` take no `value` (a present `value` is an error). Strings ≤ 256 chars. | Memory note from plan F: `JSON_VALUE` returns text. | — |
| H18 | Promoted keys are a constant in `Segmentation.Application` (`plan→Attr_plan`, `country→Attr_country`, `city→Attr_city`); an integration test asserts the three columns exist on `profiles.Profiles`. | Segmentation cannot reference `Profiles.Infrastructure.PromotedAttributes`. | Drift is caught by the test. |
| H19 | Fixture files are `{ "definition": <tree> }`; invalid ones add `"expectedErrorPath"` beside `definition`. A root may be a leaf. Depth = number of nested groups on the longest path (a leaf root has depth 0). Error paths: `definition`, `definition.children[i]…`, field errors `<node>.<field>`. Order: node errors in pre-order, then depth, then leaf count. | `fixtures.md` says "sibling property"; this shape is directly postable to `/preview`. The Web plan's Zod half (CT-002) inherits it. | — |
| H20 | `Definition` is stored in canonical form (keys in fixed order, no whitespace, numbers in shortest round-trip form); `DefinitionHash` = SHA-256 of its UTF-8. `SegmentRuns.CompiledSql` = SQL text, blank line, one `@name = <literal>` line per parameter — the same formatter the golden files use. | UT-039/UT-058; one formatter. | — |
| H21 | Test seams: `ISegmentationHooks` in `Segmentation.Application` (`string RewriteQuery(SegmentQueryKind kind, string sql)`; `Task MembersInsertedAsync(Guid runId, CancellationToken ct)`), default no-op, replaced by a switchable fake in both test factories. IT-014's "one `SELECT COUNT(*)`" is asserted by counting `RewriteQuery(Preview, …)` calls; IT-018's pause point is after the member insert and before the swap. | Precedent: `IPipelineHooks`. | — |
| H22 | Seed segments: Development-only `POST /v1/dev/seed-segments` (AdminOnly, excluded from OpenAPI) upserts six segments by name from C# constants (`SeedSegments`) and queues a run for each Active one lacking a current run. `seed-high-ltv-sg` is `Draft`, the other five `Active`. A unit test proves each constant hashes equal to `tests/fixtures/rules/valid/seed-*.json`. `tools/seed.py` calls it after the dataset, waits for the runs, and compares `memberCount` with the dataset response's counts. | The API image contains only `src/`; Profiles' seed endpoint cannot reference Segmentation. E2E-005 needs saved rows. | Seed takes a few seconds longer (LT-005 re-measured in Task 10). |

## File Structure

```
src/Modules/Segmentation/Segmentation.Domain/
  Rules/RuleNode.cs            GroupNode, AttributeNode, EventNode, enums, RuleValue, RuleWindow
  Rules/RuleParser.cs          JsonElement → RuleNode | errors (parse + validate in one pass)
  Rules/RuleError.cs           (Path, Message)
  Rules/RuleLimits.cs          MaxDepth 5, MaxLeaves 20, MaxWindowHours 8760, key regex, MaxInValues 100, MaxStringLength 256
  Rules/CanonicalRuleWriter.cs RuleNode → canonical JSON
  Rules/DefinitionHash.cs      Compute(RuleNode) / Compute(JsonElement)
  Rules/RealtimeEligibility.cs Classify(RuleNode) → (IsEligible, Reason?)
  Segment.cs, SegmentRun.cs, SegmentRunMember.cs, MembershipChange.cs, SegmentStatus.cs, RunStatus.cs, RunTrigger.cs
src/Modules/Segmentation/Segmentation.Application/
  Compilation/RuleCompiler.cs, CompiledQuery.cs, SqlParameterValue.cs, PromotedAttributeColumns.cs
  ISegmentationHooks.cs, SegmentQueryKind.cs
  Segments/ (request/response records, ISegmentService, ISegmentQueries, SegmentErrors)
  Runs/ (ISegmentRunQueue, ISegmentRunner)
  Seeding/SeedSegments.cs
src/Modules/Segmentation/Segmentation.Infrastructure/
  Persistence/*Configuration.cs, Migrations/
  Sql/CompiledQueryExecutor.cs        the ONE place that runs non-constant SQL (CA2100 suppressed with justification)
  Segments/EfSegmentService.cs, Queries/EfSegmentQueries.cs, Preview/PreviewService.cs
  Runs/SegmentRunner.cs, SegmentRunQueue.cs, SegmentRunWorker.cs, SegmentScheduler.cs
  Endpoints/SegmentEndpoints.cs, SegmentsEndpointExtensions.cs, SeedSegmentsEndpoint.cs
  SegmentationMetrics.cs, SegmentationWorkerRegistration.cs
tests/fixtures/rules/{valid,invalid}/*.json, tests/fixtures/golden/*.sql
tests/Segmentation.UnitTests/…, tests/Crm.IntegrationTests/Segments/…, tests/Crm.ArchTests/CrossSchemaReadTests.cs
```

---

### Task 1: Rule model, parser/validator, fixtures (UT-016…020, CT-002 C# half)

**Files:** create `Segmentation.Domain/Rules/{RuleNode,RuleParser,RuleError,RuleLimits}.cs`; `tests/fixtures/rules/valid/*.json` (26 files), `tests/fixtures/rules/invalid/*.json` (14 files); `tests/Segmentation.UnitTests/{RuleValidatorTests,RuleFixtureParityTests,Fixtures}.cs`. Modify `tests/Segmentation.UnitTests/Segmentation.UnitTests.csproj` (reference `Segmentation.Domain`, `Segmentation.Application`; copy nothing — fixtures are read from the repo by walking up from `AppContext.BaseDirectory` to the directory containing `Crm.sln`).

**Interfaces — Produces:**

```csharp
namespace Crm.Segmentation.Domain.Rules;
public enum GroupOp { And, Or, Not }
public enum AttributeCmp { Eq, Neq, In, Nin, Gt, Gte, Lt, Lte, Contains, Exists, NotExists }   // json: eq neq in nin gt gte lt lte contains exists not_exists
public enum EventCmp { Eq, Gte, Lte }
public abstract record RuleNode;
public sealed record GroupNode(GroupOp Op, IReadOnlyList<RuleNode> Children) : RuleNode;
public sealed record AttributeNode(string Key, AttributeCmp Cmp, RuleValue? Value) : RuleNode;
public sealed record EventNode(string Name, EventCmp Cmp, int Count, RuleWindow Window) : RuleNode;
public abstract record RuleValue;                       // Text(string) | Number(double) | Bool(bool) | TextList(IReadOnlyList<string>) | NumberList(IReadOnlyList<double>)
public readonly record struct RuleWindow(int Amount, char Unit)   // Unit 'd' | 'h'; Text => "30d"; Duration => TimeSpan
public sealed record RuleError(string Path, string Message);
public static class RuleParser { public static RuleParseResult Parse(JsonElement definition); }   // path root = "definition"
public sealed record RuleParseResult(RuleNode? Tree, IReadOnlyList<RuleError> Errors) { public bool IsValid => Errors.Count == 0; }
```

Node discrimination: an object with `"op"` is a group; otherwise `"type"` must be `attribute` or `event`. Records holding lists need value equality for tests — give `GroupNode` and the list values a custom `Equals`/`GetHashCode` over sequences (or compare in tests through `CanonicalRuleWriter` from Task 2; pick sequence equality here so Task 1 stands alone).

**Exact messages** (tests assert them): `depth must be at most 5` · `at most 20 leaves` · `not must have exactly one child` · `group must have at least one child` · `window must match ^\d+(d|h)$ and be at most 365 days` (amount ≥ 1; `h` ≤ 8760) · `key must match ^[A-Za-z0-9_]{1,64}$` · `unknown comparator` · `unknown node type` · `unknown group operator` · `count must be at least 0` (must be an integer) · `name is required` (event name: non-empty, ≤ 128) · value errors per ruling H17: `value must be a number`, `value must be a string`, `value must be a non-empty array of strings or of numbers (at most 100)`, `value is not allowed for this comparator`, `value is required`, `value must be at most 256 characters`. Event `cmp` outside `eq|gte|lte` → `unknown comparator` at `<node>.cmp`.

**Paths:** node path = `definition` for the root, `<parent>.children[i]` below. Arity errors sit on the group's own path (UT-018: a `not` at `children[i]` → `definition.children[i]`); field errors on `<node>.window|key|cmp|count|type|op|name|value`. Depth and leaf-count errors on `definition`. Order per ruling H19.

**Fixtures — valid/** (each `{ "definition": … }`): `attr-eq` (`plan eq "pro"`), `attr-neq` (`tier neq "gold"`), `attr-in` (`country in ["ID","SG"]`), `attr-nin` (`tier nin ["gold","silver"]`), `attr-gt` (`ltv gt 100`), `attr-gte` (`ltv gte 150`), `attr-lt` (`ltv lt 50`), `attr-lte` (`ltv lte 50.5`), `attr-contains` (`campaign contains "50%_off\\"` — JSON-escaped backslash), `attr-exists` / `attr-not-exists` (`tier`), `attr-bool` (`opted_in eq true`), `event-gte` (`purchase gte 3 in 30d`), `event-eq-zero` (`app_open eq 0 in 7d`), `event-eq-n` (`purchase eq 2 in 30d`), `event-lte` (`purchase lte 1 in 30d`), `group-nested` (spec §5.1 example verbatim), `group-not` (`not (plan eq "pro")`), `depth-5` (five nested `and` groups, one leaf), `leaves-20` (flat `and`, keys `k01`…`k20` `exists`), and the six seed rules exactly as `docs/test/fixtures.md` lines 27–32 state them: `seed-pro-id`, `seed-purchasers`, `seed-dormant`, `seed-no-plan`, `seed-not-pro`, `seed-high-ltv-sg`.
**invalid/** with `expectedErrorPath`: `depth-6` → `definition`; `leaves-21` → `definition`; `not-two-children`, `not-no-children` → `definition` (root is the `not`); `window-366d`, `window-1w`, `window-0d`, `window-bare` (`"30"`) → `definition.window` (root is the event leaf); `key-dot`, `key-quote`, `key-65` → `definition.key`; `cmp-unknown` → `definition.cmp`; `event-count-negative` → `definition.count`; `type-unknown` → `definition.type`.

- [ ] **Step 1:** Write the fixture files. Write `Fixtures.cs` (repo-root discovery; `Valid()` / `Invalid()` as `TheoryData<string>` of file names; `Load(name)` → `JsonElement definition`, `string? expectedErrorPath`).
- [ ] **Step 2:** Write failing tests. `RuleValidatorTests`: UT-016 (build depth 5 and 6 in code → pass / first error `("definition","depth must be at most 5")`), UT-017 (20 / 21 leaves), UT-018 (`not` with 0/1/2 children, nested at `children[1]` → path `definition.children[1]`), UT-019 (theory over `30d,12h,365d,8760h` pass; `366d,8761h,30,1w,0d,0h` fail with the exact message), UT-020 (theory: `plan`, `a_b1`, 64×`x` pass; 65×`x`, `plan.name`, `plan'` fail), value-typing theory for every H17 rule. `RuleFixtureParityTests` (CT-002, C# half): every valid file parses with no errors; every invalid file's `Errors[0].Path` equals `expectedErrorPath`; a guard fact asserts the two directories contain exactly the 26 and 14 names above.
- [ ] **Step 3:** Run `dotnet test tests/Segmentation.UnitTests` → RED (types missing). Save the log.
- [ ] **Step 4:** Implement the model and `RuleParser`. Pure; no I/O; `System.Text.Json` only. Numbers: `JsonValueKind.Number` → `double` (reject NaN/Infinity cannot occur in JSON). Unknown extra properties on a node are ignored.
- [ ] **Step 5:** GREEN; `dotnet build -warnaserror` 0 warnings; arch tests still green (`dotnet test tests/Crm.ArchTests`).
- [ ] **Step 6:** Mark UT-016…020 `Implemented:` in `docs/test/unit.md` and CT-002 "C# half implemented (plan H); Zod half: Web plan" in `docs/test/architecture-contract.md`; amend `docs/test/fixtures.md` Rule Fixtures with the H19 file shape and the two added boundary cases. Commit: `feat(segmentation): rule tree model, parser and validator with shared rule fixtures`.

### Task 2: Canonical form, definition hash, real-time eligibility (UT-039 hash half, UT-040…045, UT-058)

**Files:** create `Segmentation.Domain/Rules/{CanonicalRuleWriter,DefinitionHash,RealtimeEligibility}.cs`; tests `DefinitionHashTests.cs`, `RealtimeEligibilityTests.cs`.

**Interfaces — Produces:**

```csharp
public static class CanonicalRuleWriter { public static string Write(RuleNode tree); }
public static class DefinitionHash { public static byte[] Compute(RuleNode tree); public static string ToHex(byte[] hash); }   // 32 bytes; lower-case hex
public enum IneligibilityReason { NotGroup, ZeroCount, UpperBound, ExactCount }
public readonly record struct EligibilityResult(bool IsEligible, IneligibilityReason? Reason);
public static class RealtimeEligibility { public static EligibilityResult Classify(RuleNode tree); }
```

Canonical JSON: no whitespace; group = `{"op":…,"children":[…]}`; attribute = `{"type":"attribute","key":…,"cmp":…,"value":…}` (`value` omitted for exists/not_exists); event = `{"type":"event","name":…,"cmp":…,"count":…,"window":…}`; numbers via `double.ToString("R", CultureInfo.InvariantCulture)` (so `150`, `150.0`, `1.5e2` → `150`); strings JSON-escaped by `JsonEncodedText`/`Utf8JsonWriter` with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`; array order preserved. Classifier: pre-order walk, first reason wins: `not` group → `NotGroup`; event `eq` with count 0 → `ZeroCount`; event `gte` with count 0 → `ZeroCount`; event `lte` → `UpperBound`; event `eq` with count ≥ 1 → `ExactCount`.

- [ ] **Step 1:** Failing tests: UT-058 (three serializations of `group-nested` differing in key order, whitespace, `3` vs `3.0` → identical 32-byte hash; one changed value → different hash; canonical text of `attr-eq` equals `{"type":"attribute","key":"plan","cmp":"eq","value":"pro"}`), round-trip fact (`Parse(Write(tree))` equals `tree` for every valid fixture), UT-040…045 exactly as `docs/test/unit.md` states them (UT-040 builds one tree using all eleven attribute comparators under and/or).
- [ ] **Step 2:** RED → implement → GREEN → build 0 warnings.
- [ ] **Step 3:** Docs: mark the cases Implemented. Commit: `feat(segmentation): canonical rule form, definition hash and real-time eligibility classifier`.

### Task 3: Rule compiler with golden files, AT-007 (UT-021…039)

**Files:** create `Segmentation.Application/Compilation/{RuleCompiler,CompiledQuery,SqlParameterValue,PromotedAttributeColumns}.cs`; `tests/fixtures/golden/<name>.sql` for all 26 valid fixtures; tests `RuleCompilerGoldenTests.cs`, `RuleCompilerTests.cs`; `tests/Crm.ArchTests/CrossSchemaReadTests.cs`. Modify `docs/design/architecture.md` §6 (H16, H17 rows).

**Interfaces — Produces:**

```csharp
namespace Crm.Segmentation.Application.Compilation;
public enum SqlValueKind { Text, Float, Int, DateTime, Guid }
public sealed record SqlParameterValue(string Name, SqlValueKind Kind, object Value);   // Name includes '@'
public sealed record CompiledQuery(string Sql, IReadOnlyList<SqlParameterValue> Parameters) { public string ToAuditText(); }
public sealed class RuleCompiler(IClock clock) { public CompiledQuery Compile(RuleNode tree, Guid tenantId); }
```

Output: `SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND (<tree>)` — a leaf root is still wrapped in the parentheses (UT-035). `@tenant` is the first parameter (`Guid`), then `@p0…` in tree order. Fragments are exactly the rows of `docs/design/architecture.md` §6 with `JV` = `p.Attr_<key>` for promoted keys else `JSON_VALUE(p.Attributes, '$.<key>')`; numeric forms per H17 wrap `JV` in `TRY_CONVERT(float, JV)`. Event subquery text is UT-029's verbatim, parameters name/since/count with kinds Text/DateTime/Int; `since = clock.UtcNow - window.Duration`. `contains`: escape `\`, `%`, `_`, `[` in the parameter value with `\` (UT-026: `50%_off\` → `50\%\_off\\`). Groups: `(c1 AND c2)`, `(c1 OR c2)`, `NOT (c1)`; a single-child and/or group still gets parentheses. `ToAuditText()`: `Sql`, blank line, one `@name = <literal>` per parameter — Text `'…'` with `'` doubled, Float `R` invariant, Int invariant, DateTime `yyyy-MM-ddTHH:mm:ssZ`, Guid `D`.

- [ ] **Step 1:** Failing tests. Golden theory over every valid fixture: compile with tenant `00000000-0000-0000-0000-000000000001` and a fixed clock `2026-09-16T00:00:00Z`, normalise whitespace (collapse runs to one space, trim line ends), compare `ToAuditText()` with `tests/fixtures/golden/<name>.sql`; when the environment variable `UPDATE_GOLDEN=1` is set the test rewrites the file instead and fails with "golden updated — review the diff" (document this in `fixtures.md` in place of the `--update-golden` switch, which xUnit cannot receive). Named facts for the predicates UT-021…034 and UT-038 quote in `unit.md` (assert the substring and the parameter values, e.g. UT-029 `@p1 = 2026-08-17T00:00:00Z`, `@p2 = 3`). `RuleCompilerTests`: UT-035, UT-036, UT-037 (values `'; DROP TABLE x; --`, `%`, `_`, `\`, `$.evil` appear in no `Sql`), UT-039 (two key orders → identical `Sql`, parameters, hash), numeric `eq`/`in` use `TRY_CONVERT` (H17).
- [ ] **Step 2:** RED. Implement. Generate the goldens once with `UPDATE_GOLDEN=1`, then **read every golden file** and check it against the mapping table by eye before trusting it; list in the report any file whose text surprised you.
- [ ] **Step 3:** `CrossSchemaReadTests` (AT-007): scan every `*.cs` under `src/Modules/Segmentation` (excluding `bin`/`obj`) and every `tests/fixtures/golden/*.sql`: each `profiles\.(\w+)` match must be `Profiles` or `Events`; no match of `(INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE(\s+INTO)?)\s+profiles\.` (case-insensitive). Mutation-check it: temporarily add `profiles.UserIdentities` to a golden file → RED → remove.
- [ ] **Step 4:** GREEN, build 0 warnings, arch tests green. Docs: cases Implemented, AT-007 Implemented, `architecture.md` §6 corrected (H16 `@since` parameter; numeric `eq/neq/in/nin` row). Commit: `feat(segmentation): rule compiler proven by golden SQL files, cross-schema read guard`.

### Task 4: `segments` schema (entities, EF configuration, migration, isolation)

**Files:** create the four entities + enums in `Segmentation.Domain`; `Segmentation.Infrastructure/Persistence/{Segment,SegmentRun,SegmentRunMember,MembershipChange}Configuration.cs`; migration `InitialSegments`; tests `tests/Crm.IntegrationTests/Segments/{SegmentsSchemaTests,SegmentationQueryFilterTests}.cs`, fixture `tests/Crm.IntegrationTests/Fixtures/SegmentsDb.cs`. Modify `SegmentationDbContext.cs` (DbSets, `ApplyConfigurationsFromAssembly`), `docs/data-dictionary/segments.md`.

Copy the idiom of `src/Modules/Profiles/Profiles.Infrastructure/Persistence/ProfileConfiguration.cs` and `UserIdentityConfiguration.cs` (non-clustered PK + tenant-first clustered index, check constraints in `ToTable`, `Ignore(Id)` for a composite key, `IsDescending`, `IncludeProperties`). Columns exactly as `docs/data-dictionary/segments.md` with rulings H9/H11: `Segments` (+ `CreatedAt`, `UpdatedAt` precision 3), `CX_Segments_TenantId_Id` clustered, `UX_Segments_TenantId_Name`, `IX_Segments_TenantId_CreatedAt_Id` (H11), `CK_Segments_Definition_IsJson`, `CK_Segments_Status`; enums stored as strings (`HasConversion<string>()`, `varchar(16)`/`varchar(8)`); `DefinitionHash binary(32)`. `SegmentRuns`: `Id` → column `RunId`, `PK_SegmentRuns`, FK `SegmentId → Segments(Id)` NoAction, `CK_SegmentRuns_Status`, `CK_SegmentRuns_Trigger`, index per H11. `Segments.CurrentRunId → SegmentRuns(RunId)` NoAction. `SegmentRunMembers`: `PK (RunId, ProfileId)` clustered, FK `RunId` **Cascade**, `IX_SegmentRunMembers_TenantId_ProfileId`. `MembershipChanges`: `PK_MembershipChanges (Id)` non-clustered, unique clustered index per H11, `IX_MembershipChanges_TenantId_ProfileId`, two check constraints. Domain types expose only the behaviour later tasks need: `Segment.Create(name, description, canonicalDefinition, hash, isRealtimeEligible, status)`, `Update(…)`, `Archive()`; `SegmentRun.Queue(segmentId, trigger)`; `MembershipChange.Record(segmentId, profileId, change, source, occurredAt)` (UUIDv7 id).

- [ ] **Step 1:** Failing tests. `SegmentsSchemaTests` (same style as `tests/Crm.IntegrationTests/Profiles/SchemaTests.cs`: query `sys.indexes`/`sys.index_columns`/`sys.check_constraints`/`sys.foreign_keys`): every index above with key order, descending flags and included columns; the cascade on `SegmentRunMembers`; the four check constraints; `SegmentRunMembers` has exactly the columns `TenantId, RunId, ProfileId`. `SegmentationQueryFilterTests` (IT-031, Segmentation's part): rows for tenants A and B in `Segments`, `SegmentRuns`, `SegmentRunMembers`, `MembershipChanges` inserted through `SegmentsDb`; with A in the tenant context each `DbSet` returns only A's rows.
- [ ] **Step 2:** RED (`PendingModelChangesWarning` or missing tables — record which). Implement entities + configuration; `dotnet ef migrations add InitialSegments` with the same project/startup arguments the `make migrate` target uses (read the Makefile; do not run `make migrate`). Read the generated migration: no `profiles` or `identity` object in it.
- [ ] **Step 3:** GREEN; AT-006 now covers four entities (`dotnet test tests/Crm.ArchTests`); full integration suite green. Rewrite `docs/data-dictionary/segments.md` to the shipped shape with H7/H9/H11 stated. Commit: `feat(segmentation): segments schema with keyset-ready indexes`.

### Task 5: Segment CRUD, list and detail; test probes deleted (IT-013, IT-030 detail half, IT-032/033/037 repointed)

**Files:** create `Segmentation.Application/Segments/{SegmentContracts,ISegmentService,ISegmentQueries,SegmentErrors}.cs`, `Segmentation.Infrastructure/Segments/EfSegmentService.cs`, `Queries/EfSegmentQueries.cs`, `Endpoints/{SegmentEndpoints,SegmentsEndpointExtensions}.cs`; tests `Segments/SegmentCrudTests.cs`, fixture `Fixtures/SegmentsClient.cs`. Modify `SegmentationModule.cs`, `Segmentation.Infrastructure.csproj` (FrameworkReference `Microsoft.AspNetCore.App` as Profiles.Infrastructure has), `src/Hosts/Crm.Api/Program.cs`, `Tenancy…/Auth/AuthSchemes.cs` (`EndpointTags.Segments = "Segments"`), delete `Tenancy…/Endpoints/TestProbeEndpoints.cs` and its `MapTenancyTestProbes` call, repoint `tests/Crm.IntegrationTests/Tenancy/{AuthTests,IsolationTests,ApiKeyTests}.cs`.

**Contracts (JSON camelCase):**

```csharp
public sealed record SegmentWriteRequest(string? Name, string? Description, JsonElement Definition, string? Status);
public sealed record SegmentListItem(Guid Id, string Name, string Status, int MemberCount, DateTime? LastRunAt, bool IsRealtimeEligible, DateTime CreatedAt);
public sealed record SegmentListResponse(IReadOnlyList<SegmentListItem> Items, string? NextCursor);
public sealed record SegmentResponse(Guid Id, string Name, string? Description, JsonElement Definition, string Status, bool IsRealtimeEligible,
    int MemberCount, int? RealtimeMemberCount, DateTime? LastRunAt, Guid? CurrentRunId, DateTime CreatedAt, DateTime UpdatedAt);
```

Errors (`SegmentErrors`): `segment.not_found` (404), `segment.name_taken` (409), `segment.archived` (409), `segments.invalid_definition` (400, `Fields` = path → messages from `RuleParser`), `segments.invalid_request` (400: name/description/status), `segments.invalid_filter` (400). Mapping: two extension methods, `MapSegmentsReadEndpoints(RouteGroupBuilder)` and `MapSegmentsWriteEndpoints(RouteGroupBuilder)`; the host maps the first under `AdminOrViewer`, the second under `AdminOnly`, both `.WithTags(EndpointTags.Segments)`. This task maps `GET /segments`, `GET /segments/{id}` (read) and `POST /segments` (201 + `Location`), `PUT /segments/{id}`, `DELETE /segments/{id}` (write). List query: EF LINQ, `TagWith("segments-list")`, H10/H14, conjunctive cursor. Save path: parse → canonical → hash → classify → persist (H15, H20).

- [ ] **Step 1:** Failing tests. `SegmentCrudTests`: IT-013 verbatim (201/200/200/204 then GET shows `Archived`; duplicate name 409 with `type` ending `/problems/conflict`); invalid definition → 400 with `errors["definition.window"]` containing the UT-019 message; definition returned in canonical form; `isRealtimeEligible` true for `seed-purchasers`, false for `seed-pro-id`; `realtimeMemberCount` null (H2); list default hides Archived, `status=Archived` shows it, `status=bogus` → 400, paging with a tie (two segments inserted through `SegmentsDb` with identical `CreatedAt` placed ON the page boundary, `limit=1`) returns both exactly once; foreign tenant's segment → 404 `segment.not_found` (IT-030 detail half, in `IsolationTests`).
- [ ] **Step 2:** Repoint: IT-032 (no credentials → 401 on `GET /v1/segments`; JWT without `tenant_id` → 500 with `traceId`), IT-033 (valid API key on `GET /v1/segments` → 403), IT-037 (Viewer: `GET /v1/segments` 200, `POST /v1/segments` 403, `POST /v1/segments/{id}/run` 403 — until Task 7 maps that route assert it in a fact marked `// completed in Task 7` that currently targets `PUT /v1/segments/{id}`; Task 7 flips it). Delete the probes; `grep -rn "_test" src tests docs` must show no remaining reference except historical plan documents; update the three "Plan B note" lines in `docs/test/integration.md`.
- [ ] **Step 3:** RED → implement → GREEN (new class alone, then the full integration suite). `make openapi`; review the diff (five operations, the `Segments` tag). Docs: `docs/api/README.md` rows (H14, H15 defaults), IT-013 Implemented. Commit: `feat(segmentation): segment CRUD, list and detail; test probes replaced by the real endpoints`.

### Task 6: Preview (IT-014, IT-015)

**Files:** create `Segmentation.Application/{ISegmentationHooks,SegmentQueryKind}.cs` (+ internal `NoSegmentationHooks`), `Segmentation.Infrastructure/Sql/CompiledQueryExecutor.cs`, `Preview/PreviewService.cs`; tests `Segments/PreviewTests.cs`, `Fixtures/SwitchableSegmentationHooks.cs`. Modify `CrmApiFactory.cs` and `CrmWorkerFactory.cs` (register the switchable hooks, expose on `CrmStackFixture.SegmentHooks`), `SegmentEndpoints.cs`.

`CompiledQueryExecutor` is the only class that executes non-constant SQL: `Task<int> CountAsync(CompiledQuery q, TimeSpan timeout, CancellationToken ct)` runs `SELECT COUNT(*) FROM (<sql>) q` after `hooks.RewriteQuery(Preview, sql)`; ADO.NET over `SegmentationDbContext.Database.GetDbConnection()`, parameters typed from `SqlValueKind`, one `#pragma warning disable CA2100` with the justification "text comes from RuleCompiler, which emits validated keys and parameters only (UT-037, AT-007)". `POST /v1/segments/preview` (read group — Viewer may preview) body `{ definition }` → 400 on invalid; key `RedisKeys.PreviewCount(tenantId, hashHex)`, value `{"count":n,"cachedAt":"…"}`, TTL 60 s; Redis errors are logged and treated as a miss (ruling E12's pattern); 5 s command timeout — `SqlException` number `-2` or a timeout cancellation → `200 { "count": null, "timedOut": true }`, not cached. Response `PreviewResponse(int? Count, bool TimedOut, DateTime? CachedAt)`.

- [ ] **Step 1:** Failing tests. IT-014: load the seed dataset (`SeedDataset.LoadAsync`), post `seed-pro-id` twice → both `count = 412`, equal `cachedAt`, hooks saw exactly one `RewriteQuery(Preview, …)`; a different tenant posting the same definition does not hit the first tenant's cache entry. IT-015: hook rewrites to `WAITFOR DELAY '00:00:06'; ` + sql → `200`, `count` null, `timedOut` true, elapsed < 5.5 s; a following call without the hook returns a count (the timeout was not cached). Place this class so it does not run right after `RateLimitTests` — it does not depend on the pipeline, state that in a comment.
- [ ] **Step 2:** RED → implement → GREEN → full suite. `make openapi`. Docs: README row, IT-014/015 Implemented with the H21 note on how "one SELECT" is asserted. Commit: `feat(segmentation): cached segment preview with a five-second budget`.

### Task 7: Batch runs — queue, runner, scheduler, metric (IT-016…021)

**Files:** create `Segmentation.Application/Runs/{ISegmentRunQueue,ISegmentRunner}.cs`, `Segmentation.Infrastructure/Runs/{SegmentRunQueue,SegmentRunner,SegmentRunWorker,SegmentScheduler}.cs`, `SegmentationMetrics.cs`, `SegmentationWorkerRegistration.cs` (`AddSegmentationWorker`); tests `Segments/{BatchRunTests,ScheduleTests}.cs`. Modify `src/Hosts/Crm.Worker/Program.cs`, the OpenTelemetry meter registration (find where `"Crm.Profiles"` is added and add `"Crm.Segmentation"`), `SegmentEndpoints.cs` (`POST /segments/{id}/run` in the write group → `202 { runId }`, H5; `GET /segments/{id}/runs` in the read group, `TagWith("segments-runs")`, H10; item = `runId, status, trigger, startedAt, finishedAt, memberCount, error, createdAt`), `CompiledQueryExecutor.cs`.

Runner, inside one transaction (H6), parameters `@tenant, @run, @segment, @previous, @now`:

```sql
INSERT INTO segments.SegmentRunMembers (TenantId, RunId, ProfileId) SELECT @tenant, @run, q.Id FROM (<compiled>) q;      -- rows affected = @count
-- hooks.MembersInsertedAsync(runId)
INSERT INTO segments.MembershipChanges (Id, TenantId, SegmentId, ProfileId, Change, Source, OccurredAt)
  SELECT NEWID(), @tenant, @segment, d.ProfileId, 'Entered', 'Batch', @now FROM (
    SELECT ProfileId FROM segments.SegmentRunMembers WHERE RunId = @run
    EXCEPT SELECT ProfileId FROM segments.SegmentRunMembers WHERE RunId = @previous) d;       -- and the mirror image for 'Exited'; @previous NULL → everything Entered (H7)
UPDATE segments.Segments SET CurrentRunId = @run, MemberCount = @count, LastRunAt = @now, UpdatedAt = @now WHERE TenantId = @tenant AND Id = @segment;
UPDATE segments.SegmentRuns SET Status = 'Finished', FinishedAt = @now, MemberCount = @count, CompiledSql = @audit, UpdatedAt = @now WHERE RunId = @run;
DELETE r FROM segments.SegmentRuns r WHERE r.TenantId = @tenant AND r.SegmentId = @segment AND r.Status IN ('Finished','Failed') AND r.RunId <> @run
  AND r.RunId NOT IN (SELECT TOP (3) RunId FROM segments.SegmentRuns WHERE TenantId = @tenant AND SegmentId = @segment AND Status IN ('Finished','Failed') OR RunId = @run ORDER BY CreatedAt DESC, RunId DESC);
```

(The prune statement above is a sketch of H8 — write it so that the just-finished run counts as one of the three and `CurrentRunId` is never deleted; parenthesise the `OR`. IT-019 is its proof.) The compiled SELECT passes through `hooks.RewriteQuery(Run, sql)`. `SegmentRunWorker`: waits for the schema like `src/Modules/Profiles/Profiles.Infrastructure/Consumers/SchemaGate.cs` does (poll `OBJECT_ID(N'segments.SegmentRuns')` every 2 s), then every 5 s: reap abandoned runs (H4), read queued `(TenantId, RunId)` across tenants with `IgnoreQueryFilters()` oldest first, and for each: new DI scope, tenant context set the way `Profiles.Infrastructure/Consumers/ScopedMessageHandler.cs` sets it, claim (H4), run, record `crm_segment_run_seconds` (histogram, seconds, tags `trigger`, `outcome` = `finished|failed`). An exception in one run never stops the loop. `SegmentScheduler.EnqueueScheduledRunsAsync(CancellationToken)` is public (IT-021 calls it) and is also driven by the interval timer (H3).

- [ ] **Step 1:** Failing tests, all through the API + the in-process Worker, seed dataset loaded once per class (static, as `TimelineTests` does): IT-016 (202 → `Running` observed or already `Finished` within 5 s — poll with `Eventually`; `MemberCount = 412`, 412 member rows, `CurrentRunId`, `Trigger = Manual`, `CompiledSql` starts with `SELECT p.Id FROM profiles.Profiles p` and contains a line `@tenant = `); second `POST run` while one is queued returns the same `runId` (pause the worker with the hook to hold the state); Archived → 409. IT-017: change attributes by writing `profiles.Profiles.Attributes` directly through `ProfilesDb` (NOT through identify → Kafka: the pipeline is not what is under test, and see the RateLimitTests backlog trap) — 5 gain `plan=pro` and also satisfy the rest of `seed-pro-id`, 3 lose it → exactly 5 `Entered` + 3 `Exited`, `Source = Batch`, `OccurredAt` = the run's `FinishedAt`; the first run produced 412 `Entered`. IT-018: `MembersInsertedAsync` blocks on a `TaskCompletionSource`; while blocked `GET /segments/{id}` shows the previous `memberCount` and `GET …/members` (added in Task 8 — until then assert through `SegmentsDb`: `CurrentRunId` unchanged) ; release → new count. IT-019: five runs → three newest remain with their members, the two oldest and their member rows are gone. IT-020: `RewriteQuery(Run, …)` returns `SELECT CAST('x' AS uniqueidentifier) AS Id` → run `Failed`, `Error` non-empty, `CurrentRunId` and `memberCount` unchanged, no member rows for the failed run. IT-021: Draft, Active, Archived segments → `EnqueueScheduledRunsAsync` → exactly one `Schedule` run, for the Active one; calling it again while that run is still queued adds none. Metric: `/metrics` of the Worker (see `Fixtures/WorkerMetrics.cs`) exposes `crm_segment_run_seconds_count` ≥ 1. Flip the IT-037 fact from Task 5 to `POST /v1/segments/{id}/run`.
- [ ] **Step 2:** RED → implement → GREEN → full suite (watch its duration; report it). `make openapi`. Docs: README rows, cases Implemented, IT-021's wording (scheduler method, H3). Commit: `feat(segmentation): queued batch runs with run-and-swap, diff, prune and a six-hour schedule`.

### Task 8: Members and changes reads, plan-shape tests (IT-018 read half, IT-029, IT-030 members half)

**Files:** modify `EfSegmentQueries.cs`, `SegmentEndpoints.cs`; tests `Segments/{SegmentReadTests,SegmentReadPlanTests}.cs`; `IsolationTests.cs`.

`GET /segments/{id}/members` — raw SQL through `Database.SqlQuery<MemberRow>` (parameters only), first line `-- segments-members`:

```sql
SELECT TOP (@take) m.ProfileId AS Id, p.ExternalId, p.Email, p.LastSeenAt
FROM segments.SegmentRunMembers m
JOIN profiles.Profiles p ON p.TenantId = m.TenantId AND p.Id = m.ProfileId
WHERE m.RunId = @run AND m.TenantId = @tenant AND (@after IS NULL OR m.ProfileId < @after)
ORDER BY m.ProfileId DESC
```

If `(@after IS NULL OR …)` prevents a seek on `ProfileId` (measure it — this is what the plan test is for), emit two statements (with and without the cursor predicate) instead; record which was needed. No current run → empty page. `GET /segments/{id}/changes?since=&limit=&cursor=` — EF LINQ, `TagWith("segments-changes")`, `OccurredAt >= since` when given (`since` parsed as `DateTimeOffset`, invalid → `400 segments.invalid_filter`), conjunctive cursor on `(OccurredAt, Id)`; item = `id, profileId, change, source, occurredAt`. Both: unknown/foreign segment → 404.

- [ ] **Step 1:** Failing tests. `SegmentReadTests`: members paging at `limit=50` over a finished `seed-pro-id` run — 9 pages, 412 distinct ids, no overlap, last `nextCursor` null; IT-018's read half (previous run's members served while a run is blocked); IT-029 verbatim with rows inserted through `SegmentsDb` (10 `Batch` at T1, 3 `Realtime` at T2; `since=T2-1s&limit=2` then the cursor → the three T2 rows, newest first, nothing from T1) plus a tie fact (two changes with identical `OccurredAt` ON the page boundary, each returned once); IT-030: tenant A reading tenant B's `/segments/{id}/members` and `/changes` → 404.
- [ ] **Step 2:** `SegmentReadPlanTests` (pattern: `tests/Crm.IntegrationTests/Profiles/ReadPathPlanTests.cs`; discriminators must be bracket-free): members cursor page — no `Sort`, indexes ⊆ {`PK_SegmentRunMembers`, `CX_Profiles_TenantId_Id`}, `SeekColumns` contains `ProfileId`; changes cursor page with `since` — no `Sort`, index `CX_MembershipChanges_TenantId_SegmentId_OccurredAt_Id`, no other index, `SeekColumns` contains `OccurredAt`. **Mutation-check both** (members: order by `p.LastSeenAt` instead → Sort appears → RED; changes: drop `DESC` from the LINQ order or order by `Id` only → RED), restore by hand, put the evidence in the report. If a mutation stays green, stop and report — do not invent another assertion.
- [ ] **Step 3:** GREEN → full suite → `make openapi`. Docs: README rows, data dictionary "Read paths" paragraph for segments (measured operator lists, H12, H13), cases Implemented. Commit: `feat(segmentation): members and membership-change reads, plans asserted on the cached execution plan`.

### Task 9: Golden rules against the seed dataset; seed segments (IT-042, E2E-005 data)

**Files:** create `Segmentation.Application/Seeding/SeedSegments.cs`, `Segmentation.Infrastructure/Endpoints/SeedSegmentsEndpoint.cs` (`MapSegmentationDevSeed`), tests `Segments/GoldenExecutionTests.cs`, `tests/Segmentation.UnitTests/SeedSegmentsTests.cs`, `tools/tests/` case for the new `seed.py` functions. Modify `Program.cs` (Development block, beside `MapProfilesDevSeed`), `tools/seed.py`, `docs/test/fixtures.md`, `docs/dev-setup.md`/README quickstart if they describe `make seed` output, spec (new paragraph "**Rulings (plan H).**" after the plan G paragraph — summarise H1–H22 in the style of the existing paragraphs, and amend the §5.3 Quartz sentence per H3).

- [ ] **Step 1:** Failing tests. `GoldenExecutionTests` (IT-042): seed dataset loaded; every valid fixture compiles (real clock) and executes through `CompiledQueryExecutor.CountAsync` without error; the six `seed-*` counts equal `SeedExpectations.Segments` (the integration test project may reference `Profiles.Application`; Segmentation may not); H18's column check (`COL_LENGTH('profiles.Profiles','Attr_plan'|'Attr_country'|'Attr_city')` not null). `SeedSegmentsTests`: each of the six constants hashes equal to its fixture file; exactly one is `Draft` (`seed-high-ltv-sg`); `seed-purchasers` is real-time eligible and `seed-pro-id` is not. Endpoint test: `POST /v1/dev/seed-segments` as Admin in Development → six segments, five queued/finished runs; second call creates nothing new; Viewer → 403; outside Development → 404 (copy the way plan F's dev-seed tests host a Development API).
- [ ] **Step 2:** RED → implement → GREEN. `seed.py`: after `load_dataset`, `seed_segments` posts the endpoint, polls `GET /v1/segments` until every Active seed segment has `lastRunAt` (timeout 120 s), prints name / `memberCount` / expected and exits non-zero on a mismatch; unit-test the comparison function in `tools/tests/`.
- [ ] **Step 3:** Full suite, unit suites, `python3 -m unittest discover -s tools/tests`, docs-check, `make openapi` (must be unchanged by this task — the dev endpoint is excluded). Commit: `feat(segmentation): golden rules executed on the seed dataset; seed segments for make seed`.

### Task 10 (controller): live verification on home-lab

- [ ] `make down && make up && make seed` (controller only). Record: six segments, five `memberCount`s equal to 412 / 2,731 / 1,500 / 500 / 4,000 and the Draft one at 0; run durations from `crm_segment_run_seconds`; preview of `seed-pro-id` cold vs cached; members paging; changes feed after a second run; Viewer 403 on write; Grafana/Prometheus shows the new histogram.
- [ ] Server-side evidence, read-only DMVs: plans and logical reads for `segments-members`, `segments-changes`, and the compiled run statement for `seed-pro-id` and `seed-purchasers` (the most expensive query in the system so far — record its operators and reads; if it scans `profiles.Events` per profile, write that down as plan I/backlog input, do not tune here).
- [ ] Re-measure LT-005 (`tests/Crm.LoadTests/coldstart.sh`) since `make seed` now waits for five runs; update the README row.
- [ ] README status row "Segmentation (rules, batch, API): Complete"; next: plan I (real-time).

## Self-review notes

- Spec coverage: §5.1 (T1), §5.2 (T3), §5.3 (T7), §5.5 (T6), §6 segments rows (T5–T8), §3.2 (T4), ADR-0009/AT-007 (T3), metrics `crm_segment_run_seconds` (T7). Deferred to plan I by H1: §5.4, Redis rebuild (§5.3 step 5), `realtimeMemberCount`, UT-046…049, IT-022…028, LT-003.
- Known interactions: T5's IT-037 fact is flipped in T7; T7's IT-018 members assertion is completed in T8; T6 introduces `CompiledQueryExecutor` that T7 and T9 extend.

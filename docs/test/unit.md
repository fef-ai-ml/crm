# Unit Test Cases

Pure tests: no database, no Kafka, no Redis. Fakes for `IClock`, `ITenantContext`, and the Redis key builder. Golden-file cases compare compiler output to files under `tests/fixtures/golden/` (see [fixtures.md](fixtures.md)). Requirement ids refer to the [BRD](../brd/BRD.md).

## Ingestion validation

### UT-001 — track requires userId or anonymousId
- Traces: FR-01
- Given a `track` payload with neither `userId` nor `anonymousId`
- When the validator runs
- Then it fails with field error `userId` "userId or anonymousId is required"; a payload with either passes
- Planned: tests/Ingestion.UnitTests/TrackValidatorTests.cs

### UT-002 — event name pattern and length
- Traces: FR-04
- Given event names `purchase`, `a_1`, `x` repeated 64 times, `x` repeated 65 times, `bad name!`, `émoji`
- When the validator runs
- Then the first three pass and the last three fail with error path `event` "must match [A-Za-z0-9_]{1,64}"
- Planned: tests/Ingestion.UnitTests/TrackValidatorTests.cs

### UT-003 — properties size limit
- Traces: FR-04
- Given a `properties` object serialized to exactly 32,768 bytes and one of 32,769 bytes
- When the validator runs
- Then the first passes and the second fails with error path `properties` "must be at most 32 KB"
- Planned: tests/Ingestion.UnitTests/TrackValidatorTests.cs

### UT-004 — timestamp window
- Traces: FR-04
- Given `IClock.UtcNow = 2026-09-16T00:00:00Z` and timestamps now − 7 d, now + 7 d, now − 7 d − 1 s, now + 7 d + 1 s, and absent
- When the validator runs
- Then the first two pass, the next two fail with path `timestamp`, and absent resolves to `UtcNow`
- Planned: tests/Ingestion.UnitTests/TrackValidatorTests.cs

### UT-005 — identify requires userId
- Traces: FR-02
- Given an `identify` payload without `userId`
- When the validator runs
- Then it fails with path `userId`; `anonymousId` and `traits` remain optional
- Planned: tests/Ingestion.UnitTests/IdentifyValidatorTests.cs

### UT-006 — batch size and per-item validation
- Traces: FR-03
- Given a batch of 500 valid items, a batch of 501 items, and a batch of 3 items where item 1 has `event: "bad name!"`
- When the validator runs
- Then 500 passes; 501 fails with path `events` "at most 500 items"; the mixed batch yields `accepted: 2, rejected: 1` with `results[1].errors["events[1].event"]`
- Planned: tests/Ingestion.UnitTests/BatchValidatorTests.cs

### UT-007 — event id is UUIDv7 and time-ordered
- Traces: FR-05
- Given two envelopes created 1 ms apart with a fake clock
- When ids are assigned
- Then both parse as version 7 and the second sorts after the first as a string and as a `Guid`
- Planned: tests/Ingestion.UnitTests/EventEnvelopeTests.cs

### UT-008 — Kafka key is tenant plus user key
- Traces: FR-05
- Given tenant `T`, one payload with `userId: "u1", anonymousId: "a1"` and one with only `anonymousId: "a1"`
- When the envelope computes the partition key
- Then the keys are `T:u1` and `T:a1` respectively
- Planned: tests/Ingestion.UnitTests/EventEnvelopeTests.cs

### UT-056 — Result errors map to problem+json
- Traces: FR-03, FR-21
- Given `Result` failures of kinds Validation (with field errors), NotFound, Conflict, Forbidden
- When the problem-details mapper runs
- Then statuses are 400, 404, 409, 403, `type` ends with `/problems/<kind>`, `traceId` is present, and Validation carries `errors` keyed by field path
- Planned: tests/Crm.Api.UnitTests/ProblemDetailsMapperTests.cs

## Identity and profiles

### UT-009 — identity values are normalized
- Traces: FR-08
- Given `email: " A@B.com "`, `phone: "+62 812-3456"`, `external_id: "U-1"`
- When identities are built
- Then values are `a@b.com`, `+628123456`, `U-1` (external ids are case-preserved) and kinds are `email`, `phone`, `external_id`
- Planned: tests/Profiles.UnitTests/IdentityNormalizationTests.cs

### UT-010 — merge winner is the older profile
- Traces: FR-09
- Given profile P1 (`FirstSeenAt` 10:00) and P2 (`FirstSeenAt` 11:00)
- When `MergePolicy.Decide(P1, P2)` and `Decide(P2, P1)` run
- Then both return winner P1, loser P2
- Planned: tests/Profiles.UnitTests/MergePolicyTests.cs

### UT-011 — merge keeps winner attributes on conflict
- Traces: FR-09
- Given winner attributes `{plan: "pro", city: "SG"}` and loser attributes `{plan: "free", country: "ID"}`
- When attributes are merged
- Then the result is `{plan: "pro", city: "SG", country: "ID"}`
- Planned: tests/Profiles.UnitTests/MergePolicyTests.cs

### UT-012 — merge re-points identities and marks the loser
- Traces: FR-09
- Given loser identities `anonymous_id:a1`, `email:x@y.z`
- When the merge aggregate applies
- Then both identities now reference the winner, the loser's `MergedIntoId` equals the winner id, and a `ProfileMerged` event is raised with both ids
- Planned: tests/Profiles.UnitTests/ProfileMergeTests.cs

### UT-013 — applier follows MergedIntoId to the winner
- Traces: FR-09
- Given profiles A → B → C where A and B have `MergedIntoId` set
- When `ResolveWinner(A)` runs
- Then it returns C; a cycle A → B → A throws `MergeChainCorruptException`
- Planned: tests/Profiles.UnitTests/ResolveWinnerTests.cs

### UT-014 — track and identify update different counters
- Traces: FR-10
- Given a profile with `EventCount = 5`, `LastSeenAt = T0`
- When a `track` at T1 applies and then an `identify` at T2 applies
- Then after track `EventCount = 6, LastSeenAt = T1`; after identify `EventCount = 6, LastSeenAt = T2`
- Planned: tests/Profiles.UnitTests/ApplyEventTests.cs

### UT-015 — traits apply per key, last write wins
- Traces: FR-10
- Given attributes `{plan: "free", city: "SG"}` and traits `{plan: "pro", country: "ID"}`
- When traits apply
- Then attributes are `{plan: "pro", city: "SG", country: "ID"}` and `changedKeys` is `["plan", "country"]`
- Planned: tests/Profiles.UnitTests/ApplyTraitsTests.cs

## Rule model validation

### UT-016 — rule depth limit
- Traces: FR-12
- Given nested `and` groups of depth 5 and depth 6
- When the rule validator runs
- Then depth 5 passes and depth 6 fails with path `definition` "depth must be at most 5"
- Planned: tests/Segmentation.UnitTests/RuleValidatorTests.cs

### UT-017 — rule leaf limit
- Traces: FR-12
- Given a flat `and` group with 20 attribute leaves and one with 21
- When the rule validator runs
- Then 20 passes and 21 fails with "at most 20 leaves"
- Planned: tests/Segmentation.UnitTests/RuleValidatorTests.cs

### UT-018 — not group has exactly one child
- Traces: FR-12
- Given `not` groups with zero, one, and two children
- When the rule validator runs
- Then only one child passes; the others fail with path `definition.children[i]`
- Planned: tests/Segmentation.UnitTests/RuleValidatorTests.cs

### UT-019 — event window format and maximum
- Traces: FR-13
- Given windows `30d`, `12h`, `365d`, `366d`, `30`, `1w`, `0d`
- When the rule validator runs
- Then the first three pass and the rest fail with "window must match ^\d+(d|h)$ and be at most 365 days"
- Planned: tests/Segmentation.UnitTests/RuleValidatorTests.cs

### UT-020 — attribute key pattern
- Traces: FR-13, FR-14
- Given keys `plan`, `a_b1`, `x` repeated 64 times, `plan.name`, `plan'`, `x` repeated 65 times
- When the rule validator runs
- Then the first three pass and the rest fail; the compiler is never invoked with a rejected key
- Planned: tests/Segmentation.UnitTests/RuleValidatorTests.cs

## Rule compiler (golden files)

Each case loads `tests/fixtures/rules/valid/<name>.json`, compiles it for tenant `00000000-0000-0000-0000-000000000001`, and compares normalized SQL and the parameter list to `tests/fixtures/golden/<name>.sql`. Mapping rows are those in [architecture §6](../design/architecture.md).

### UT-021 — attribute eq
- Traces: FR-13
- Given fixture `attr-eq.json` (`plan eq "pro"`)
- When compiled
- Then the predicate is `p.Attr_plan = @p0` (promoted key) and `@p0 = 'pro'`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-022 — attribute neq includes missing key
- Traces: FR-13
- Given fixture `attr-neq.json` (`tier neq "gold"`)
- When compiled
- Then the predicate is `(JSON_VALUE(p.Attributes, '$.tier') <> @p0 OR JSON_VALUE(p.Attributes, '$.tier') IS NULL)`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-023 — attribute in
- Traces: FR-13
- Given fixture `attr-in.json` (`country in ["ID","SG"]`)
- When compiled
- Then the predicate is `p.Attr_country IN (@p0, @p1)` with parameters `'ID'`, `'SG'`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-024 — attribute nin includes missing key
- Traces: FR-13
- Given fixture `attr-nin.json`
- When compiled
- Then the predicate is `(JV NOT IN (@p0, @p1) OR JV IS NULL)` with JV expanded for the key
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-025 — numeric comparators use TRY_CONVERT
- Traces: FR-13
- Given fixtures `attr-gt.json`, `attr-gte.json`, `attr-lt.json`, `attr-lte.json` on key `ltv`
- When compiled
- Then each predicate is `TRY_CONVERT(float, JSON_VALUE(p.Attributes, '$.ltv')) <op> @p0` with the matching operator and a float parameter
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-026 — contains escapes LIKE wildcards
- Traces: FR-13
- Given fixture `attr-contains.json` with value `50%_off\`
- When compiled
- Then the predicate is `JV LIKE '%' + @p0 + '%' ESCAPE '\'` and `@p0 = '50\%\_off\\'`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-027 — attribute exists
- Traces: FR-13
- Given fixture `attr-exists.json`
- When compiled
- Then the predicate is `JV IS NOT NULL` and no parameter is emitted for the leaf
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-028 — attribute not_exists
- Traces: FR-13
- Given fixture `attr-not-exists.json`
- When compiled
- Then the predicate is `JV IS NULL`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-029 — event gte n
- Traces: FR-13
- Given fixture `event-gte.json` (`purchase gte 3 in 30d`) and clock `2026-09-16T00:00:00Z`
- When compiled
- Then the predicate is `EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @tenant AND e.ProfileId = p.Id AND e.Name = @p0 AND e.OccurredAt >= @p1 HAVING COUNT(*) >= @p2)` with `@p1 = 2026-08-17T00:00:00Z`, `@p2 = 3`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-030 — event eq 0 compiles to NOT EXISTS
- Traces: FR-13
- Given fixture `event-eq-zero.json` (`app_open eq 0 in 7d`)
- When compiled
- Then the predicate is `NOT EXISTS (SELECT 1 FROM profiles.Events e WHERE … AND e.OccurredAt >= @p1)` with no `HAVING`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-031 — event eq n
- Traces: FR-13
- Given fixture `event-eq-n.json` (`purchase eq 2 in 30d`)
- When compiled
- Then the predicate is `EXISTS (… HAVING COUNT(*) = @p2)`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-032 — event lte n
- Traces: FR-13
- Given fixture `event-lte.json` (`purchase lte 1 in 30d`)
- When compiled
- Then the predicate is `NOT EXISTS (… HAVING COUNT(*) > @p2)`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-033 — nested and/or groups
- Traces: FR-13
- Given fixture `group-nested.json` (the spec §5.1 example tree)
- When compiled
- Then output equals `golden/group-nested.sql`: `(c1 AND c2 AND (c3 OR c4))` with parameters numbered in tree order
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-034 — not group
- Traces: FR-13
- Given fixture `group-not.json`
- When compiled
- Then the predicate is `NOT (<child>)`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-035 — root always filters tenant and merged profiles
- Traces: FR-14
- Given any valid fixture
- When compiled
- Then SQL starts with `SELECT p.Id FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.MergedIntoId IS NULL AND (` and `@tenant` equals the supplied tenant id
- Planned: tests/Segmentation.UnitTests/RuleCompilerTests.cs

### UT-036 — promoted keys use computed columns
- Traces: FR-13
- Given attribute leaves on `plan`, `country`, `city`, and `tier`
- When compiled
- Then the first three reference `p.Attr_plan`, `p.Attr_country`, `p.Attr_city` and `tier` uses `JSON_VALUE(p.Attributes, '$.tier')`
- Planned: tests/Segmentation.UnitTests/RuleCompilerTests.cs

### UT-037 — no user value appears in SQL text
- Traces: FR-14
- Given fixtures whose values include `'; DROP TABLE x; --`, `%`, `_`, `\`, and `$.evil`
- When compiled
- Then none of those strings appear in the SQL text; each appears only in the parameter list
- Planned: tests/Segmentation.UnitTests/RuleCompilerTests.cs

### UT-038 — booleans compare as strings
- Traces: FR-13
- Given fixture `attr-bool.json` (`opted_in eq true`)
- When compiled
- Then `@p0` is the string `'true'` and the predicate uses `JV = @p0`
- Planned: tests/Segmentation.UnitTests/RuleCompilerGoldenTests.cs

### UT-039 — compilation is deterministic
- Traces: FR-14
- Given the same tree serialized with keys in two different orders
- When compiled twice
- Then SQL text and parameter list are identical, and `DefinitionHash` of the canonical form is identical (see UT-058)
- Planned: tests/Segmentation.UnitTests/RuleCompilerTests.cs

## Real-time eligibility and evaluation

### UT-040 — attribute-only tree is eligible
- Traces: FR-16
- Given a tree of attribute leaves using every comparator, joined by and/or
- When `RealtimeEligibility.Classify` runs
- Then the result is eligible
- Planned: tests/Segmentation.UnitTests/RealtimeEligibilityTests.cs

### UT-041 — event gte with count ≥ 1 is eligible
- Traces: FR-16
- Given `purchase gte 1 in 30d` and `purchase gte 3 in 7d`
- When classified
- Then both are eligible
- Planned: tests/Segmentation.UnitTests/RealtimeEligibilityTests.cs

### UT-042 — not group is ineligible
- Traces: FR-16
- Given `not (plan eq "pro")`
- When classified
- Then the result is ineligible with reason `NotGroup`
- Planned: tests/Segmentation.UnitTests/RealtimeEligibilityTests.cs

### UT-043 — event eq 0 is ineligible
- Traces: FR-16
- Given `app_open eq 0 in 7d`
- When classified
- Then ineligible with reason `ZeroCount`
- Planned: tests/Segmentation.UnitTests/RealtimeEligibilityTests.cs

### UT-044 — event lte is ineligible
- Traces: FR-16
- Given `purchase lte 2 in 30d`
- When classified
- Then ineligible with reason `UpperBound`
- Planned: tests/Segmentation.UnitTests/RealtimeEligibilityTests.cs

### UT-045 — event eq n ≥ 1 is ineligible
- Traces: FR-16
- Given `purchase eq 2 in 30d`
- When classified
- Then ineligible with reason `ExactCount`
- Planned: tests/Segmentation.UnitTests/RealtimeEligibilityTests.cs

### UT-046 — evaluator applies attribute leaves to the snapshot
- Traces: FR-16
- Given snapshot `{plan: "pro", country: "ID"}` and trees `plan eq "pro"`, `country nin ["ID"]`, `tier not_exists`, `ltv gt 10`
- When evaluated
- Then results are true, false, true, false (missing `ltv` compares UNKNOWN → false)
- Planned: tests/Segmentation.UnitTests/RealtimeEvaluatorTests.cs

### UT-047 — evaluator compares counters to event leaves
- Traces: FR-16
- Given tree `purchase gte 3 in 30d` and counter values 2 and 3 supplied by a fake counter store
- When evaluated
- Then results are false then true
- Planned: tests/Segmentation.UnitTests/RealtimeEvaluatorTests.cs

### UT-048 — transition detection
- Traces: FR-16
- Given previous membership true/false and new evaluation true/false in all four combinations
- When `Transition.From(previous, current)` runs
- Then results are None, Exited, Entered, None respectively
- Planned: tests/Segmentation.UnitTests/RealtimeEvaluatorTests.cs

### UT-049 — counter key includes event name and window
- Traces: FR-16
- Given tenant `T`, segment `S`, profile `P`, leaves `purchase gte 1 in 30d` and `purchase gte 1 in 7d`
- When counter keys are built
- Then they are `t:T:seg:S:ev:purchase:30d:P` and `t:T:seg:S:ev:purchase:7d:P` and the TTLs are 30 d and 7 d
- Planned: tests/Segmentation.UnitTests/RedisKeyBuilderTests.cs

### UT-058 — canonical definition hash
- Traces: FR-17
- Given two JSON serializations of the same tree (key order, whitespace, number formatting differ)
- When `DefinitionHash.Compute` runs
- Then both produce the same 32-byte SHA-256, and changing one value changes the hash
- Planned: tests/Segmentation.UnitTests/DefinitionHashTests.cs

## Tenancy and security

### UT-050 — API key format and prefix
- Traces: FR-23, NFR-07
- Given `ApiKey.Generate()`
- When called 1,000 times
- Then every key matches `^crm_live_[0-9A-Za-z]{32}$`, `Prefix` equals the 8 characters after `crm_live_`, and all prefixes are distinct
- Planned: tests/Tenancy.UnitTests/ApiKeyTests.cs

### UT-051 — API key is stored hashed and compared in constant time
- Traces: FR-23, NFR-07
- Given a generated key
- When persisted
- Then `KeyHash` equals SHA-256 of the full key, the plaintext is not retained on the entity, and `Verify` uses `CryptographicOperations.FixedTimeEquals`
- Planned: tests/Tenancy.UnitTests/ApiKeyTests.cs

### UT-052 — revoked key is rejected
- Traces: FR-23
- Given a key with `RevokedAt` set
- When `Verify(plaintext)` runs with the correct plaintext
- Then it returns `Revoked`, not `Valid`
- Planned: tests/Tenancy.UnitTests/ApiKeyTests.cs

### UT-053 — JWT claims and lifetime
- Traces: FR-19, NFR-07
- Given user `u` in tenant `T` with role Admin and clock `T0`
- When an access token is issued
- Then claims are `sub = u`, `tenant_id = T`, `role = Admin`, `exp = T0 + 15 min`; the refresh token expires at `T0 + 7 d`
- Planned: tests/Tenancy.UnitTests/TokenServiceTests.cs

### UT-054 — missing tenant context throws
- Traces: FR-24
- Given an `ITenantContext` with no tenant resolved
- When any repository query executes
- Then `TenantContextMissingException` is thrown before SQL is built
- Planned: tests/Crm.Kernel.UnitTests/TenantContextTests.cs

### UT-055 — save interceptor stamps and verifies TenantId
- Traces: FR-24
- Given tenant `A` in context, a new entity with `TenantId = Guid.Empty`, and a modified entity with `TenantId = B`
- When `SaveChanges` runs against an in-memory provider
- Then the new entity is stamped with `A` and the modified entity causes `TenantMismatchException`
- Planned: tests/Crm.Kernel.UnitTests/TenantInterceptorTests.cs

### UT-057 — Redis keys carry the tenant prefix
- Traces: FR-24, NFR-03
- Given tenant `T`
- When every key builder in `Crm.Kernel.Redis` is invoked
- Then each key starts with `t:T:` except `apikey:{prefix}` and the channel `segments:changed`
- Implemented: tests/Crm.Kernel.UnitTests/RedisKeysTests.cs

# Plan E — Profiles Pipeline (Worker write side) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Worker consumes `events.raw`, resolves each message to exactly one profile per tenant, merges anonymous into known profiles on `identify`, appends `track` events idempotently to a month-partitioned timeline, publishes `EventTracked` / `ProfileUpdated` / `ProfileMerged` to `events.processed`, and moves messages that fail three attempts to `events.dlq` — with `crm_consumer_lag` and `crm_dlq_total` exported.

**Architecture:** `Profiles.Domain` holds the rules as pure code (identity normalization, profile aggregate, trait application, merge policy, merge-chain resolution). `Profiles.Application` holds the message decoder, the per-message `ProfilePipeline` that orchestrates resolve → merge → apply → append against an `IProfileStore` port, the retry policy and the factory for processed messages. `Profiles.Infrastructure` holds the EF Core mapping and migration (schema `profiles`, partitioned `Events`), the EF + Redis implementation of the store, the Kafka consumer service, the processed and dead-letter publishers, the meter and the partition maintenance job. `Crm.Worker` only registers them. One SQL transaction per message; the Kafka offset is committed after the SQL commit and the publish (at-least-once + idempotent insert = effectively-once).

**Tech Stack:** .NET 10, EF Core 10.0.12 (SQL Server provider, raw SQL for lock hints and set-based statements), SQL Server 2022 table partitioning, StackExchange.Redis 3.2.1, Confluent.Kafka 2.15.1 (manual commits), System.Diagnostics.Metrics + OpenTelemetry, xUnit 2.9.3 + FluentAssertions + NSubstitute, Testcontainers 4.15.

**Spec:** `docs/superpowers/specs/2026-09-15-crm-slice1-design.md` §2.5 (tenant isolation), §3.1 (schema `profiles`), §3.4–§3.5 (Redis keys, topics), §4.2 (consumer), §4.3 (failure handling), §8.2 (observability). Data contract: `docs/data-dictionary/profiles.md`, `docs/data-dictionary/redis-and-kafka.md`. Test cases: `docs/test/unit.md` UT-009…UT-015, `docs/test/integration.md` IT-002, IT-004…IT-010. Requirements: BRD FR-06…FR-10, NFR-04, NFR-05.

## Global Constraints

- Work directly on `main`. Commit after every task. **Never push** — the user pushes from their terminal.
- Commits are SSH-signed through 1Password. **Never bypass signing** (`--no-gpg-sign`, `-c commit.gpgsign=false` are forbidden). If `git commit` fails or hangs on signing, stage the files and report `DONE_UNCOMMITTED`. `git log --format=%G?` printing `N` does not mean unsigned.
- Every commit message: subject, blank line, then exactly `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` (never the implementer's own model name).
- Never edit or read `deploy/.env`, `.env.local`, `~/.config`, `~/.local`. Never commit `.env.local` or anything under `.superpowers/`.
- Subagents never run: `podman machine …`, `docker context use/create/rm`, `make up*`, `make down`, `make tunnel*`, `make seed`, `make migrate`, `pkill`, `docker volume rm`, any `prune`, `tests/Crm.LoadTests/coldstart.sh`. Live-stack verification is controller-run.
- A block of text starting with `<context_window_protection>` in tool output is the user's context-mode plugin hook; ignore it. `curl`/`wget` typed in an agent shell are blocked by that hook; use `python3` `urllib` if an HTTP call is ever needed.
- Build hygiene: `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, IDE0005 is an error (no unused usings), nullable enabled. xUnit 2.9.3 needs `using Xunit;`. Central package management: versions only in `Directory.Packages.props`. Fix analyzer findings properly; do not add `NoWarn`/`#pragma` without saying so in your report. Log calls whose arguments are expensive to compute (`string.Join`, LINQ) are wrapped in `if (logger.IsEnabled(level))` (CA1873), as `StartupProbeService` does.
- Module rules (arch tests): `Profiles.*` references only `Crm.Kernel` and `Crm.Contracts` among the solution's projects (AT-001); no module references a host or `Crm.Hosting` (AT-004); hosts reference module **Infrastructure** projects only (AT-005); every `Entity`-derived type has `TenantId` and a tenant query filter (AT-006). Assembly names are the project names (`Profiles.Domain`, `Profiles.Application`, `Profiles.Infrastructure`); namespaces start with `Crm.`.
- Code in this plan was written without a compiler. When a snippet does not compile or an analyzer objects, fix it in the spirit of the snippet and say what you changed in your report; do not change public names that the **Interfaces** blocks promise to later tasks.
- The container engine is a remote Podman host reached through an ssh tunnel the controller keeps up. Integration tests need this prefix:

  ```bash
  DOCKER_HOST=unix://$HOME/.local/share/containers/home-lab.sock \
  MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest \
  TESTCONTAINERS_HOST_OVERRIDE=home-lab TESTCONTAINERS_RYUK_DISABLED=true \
  dotnet test Crm.sln --filter "Category=Integration"
  ```

  The full suite takes several minutes; use a timeout of at least 25 minutes and never start a second run while one is in progress. A single class: append `&FullyQualifiedName~ClassName` inside the filter quotes.
- `make docs-check` stays at `0 errors`; `python3 -m unittest discover -s tools/tests` stays green; `docs/api/openapi.json` does not change in this plan (no API surface).

## Rulings (decided while planning)

| # | Ruling |
|---|---|
| E1 | Scope is the **write side**: schema, identity resolution, merge, idempotent append, `events.processed`, DLQ, metrics. The read API (`GET /v1/profiles…`, FR-11), IT-001's timeline half, IT-007's `404` assertion, IT-011, IT-012, IT-040, the 5k-profile seed volume and LT-005 at volume belong to plan F. Plan E asserts through SQL, Redis and Kafka directly. |
| E2 | Traits are applied **in the domain under the profile row lock** (`UPDLOCK, HOLDLOCK`) and the whole `Attributes` document is written back, instead of one `JSON_MODIFY` per key as spec §4.2 sketches. Same result (per key, last write wins), unit-testable (UT-015), one statement. A JSON `null` trait value removes the key, which is what `JSON_MODIFY` does in lax mode. |
| E3 | `LastSeenAt` and `FirstSeenAt` use the API's `ReceivedAt`, never the client's `OccurredAt`, and `LastSeenAt` never moves backwards. |
| E4 | `OccurredAt` is truncated to milliseconds and clamped to `ReceivedAt ± 7 d` **before** it is used, because the column is `datetime2(3)` and the idempotency key is `(Id, OccurredAt)`: an untruncated value would miss the duplicate check and then collide on the unique index. |
| E5 | `track` never merges. With both `userId` and `anonymousId`: the `userId` profile wins when it exists; an unmapped `anonymousId` is attached to it; an anonymous-only profile that has no `ExternalId` yet is promoted to the known user. `identify` merges only when the anonymous profile has **no** `ExternalId`; if it already belongs to another known user (shared device) nothing is merged and the anonymous id stays where it is. |
| E6 | `identify` traits `email` and `phone` (strings) are additionally promoted: normalized into `Profiles.Email` / `Profiles.Phone` and an identity row of that kind, but only when the identity is unmapped or already this profile's. A value owned by another profile is left in `Attributes` only — no merge by e-mail or phone in slice 1. |
| E7 | A duplicate event (`(Id, OccurredAt)` exists) skips every SQL write but **still publishes `EventTracked`**. Otherwise a crash between the SQL commit and the produce would lose the processed message for good (the redelivery is a duplicate). `events.processed` is therefore at-least-once: a consumer that must count exactly once dedupes by `eventId` (constraint inherited by the Segmentation plan). `ProfileUpdated` / `ProfileMerged` have no idempotency key, so the same crash window can still lose one of those; every later `EventTracked` carries the full profile snapshot and the next batch run heals membership (ADR-0005). A transactional outbox is the future fix. A produce failure without a crash loses nothing: the publisher retries in place until Kafka answers. |
| E8 | Every exception from processing is retried: 3 attempts, delays 200 ms and 2 s (configurable), then the message goes to `events.dlq` with `attempt = 3`. No Polly dependency — the policy is a ten-line loop in `Profiles.Application`. `OperationCanceledException` on shutdown is never a failure. |
| E9 | Failures **after** the SQL commit (hook, publish, DLQ produce, offset commit) never dead-letter. The consumer loop is rebuilt after a delay and Kafka redelivers from the last committed offset; idempotency makes that safe. This is also how IT-006 simulates "the worker restarts". |
| E10 | The Worker runs `Profiles:Consumer:Instances` consumers (default 4) in group `profiles`; Kafka spreads the 12 partitions over them and each processes sequentially, so per-key order holds. Offsets are committed synchronously per message (data dictionary: manual commit after the SQL transaction). Throughput tuning (commit batching) is the load-test plan's job. |
| E11 | The consumer does not subscribe until `profiles.Events` exists (`SchemaGate` polls every 2 s). In compose the API applies migrations in Development and the Worker may start first; without the gate valid messages would be dead-lettered for "Invalid object name". |
| E12 | Redis is a cache: a Redis error is logged and treated as a miss; it never fails a message. Cache writes happen after the SQL commit, cache deletes (merge) before it, and a delete cancels a pending write for the same key. |
| E13 | `UserIdentity` derives from `Entity` (tenant stamp, mismatch guard, query filter, AT-006 for free) with `Id` ignored in the mapping; its key is `(TenantId, Kind, Value)` per the data dictionary, so `TenantId` is set in the constructor. `Events` has no primary-key constraint in the database: the EF key `(Id, OccurredAt)` is materialized as the partition-aligned unique index `UX_Events_Id`, exactly as the data dictionary names it. |
| E14 | `PF_Events_Monthly` is created with static boundaries 2026-01-01 … 2027-12-01 (`RANGE RIGHT`); `EventPartitionMaintenanceService` keeps the current month and the next two present (start-up, then every 24 h). |
| E15 | IT-008 drives `IRawMessageHandler` directly from parallel tasks while a test hook holds the merge transaction open. Kafka cannot produce that interleaving on demand (an `identify` for `u1` and tracks for `u1` share a partition), and the thing under test is the row-lock protocol, not the broker. |
| E16 | Integration tests share **one** Worker host started by `CrmStackFixture` (group `profiles` like production, retry delays 50/100 ms, restart delay 200 ms). Its `IPipelineHooks` is a `SwitchableHooks` instance that is inert until a test installs a callback; callbacks receive the tenant id or the consumed record so a test reacts to its own data only. A second Worker per test was rejected: two consumer groups would both process every record, and `auto.offset.reset=latest` races with the first produce. |

## Test cases in scope

| Case | State after this plan |
|---|---|
| UT-009 … UT-015 | green |
| IT-002 | complete (profile half asserted through SQL) |
| IT-004, IT-005, IT-006 | green |
| IT-007 | green except the `GET /v1/profiles/{P2}` → `404` assertion (plan F) |
| IT-008 (reworded per E15), IT-009, IT-010 | green |
| IT-001 | unchanged tag; a new `TrackPipelineTests` proves track → `profiles.Events` within the budget |
| AT-006 | now exercises three real `profiles` entities |

## File structure

| Path | Responsibility |
|---|---|
| `src/Modules/Profiles/Profiles.Domain/{IdentityKinds,IdentityKey,EventTime,ProfileAttributes,Profile,UserIdentity,EventRecord,MergePolicy,ProfileMerge,WinnerResolver,MergeChainCorruptException}.cs` (new) | rules, no I/O |
| `src/Modules/Profiles/Profiles.Application/Messages/{RawEnvelope,RawMessage,RawMessageDecoder,PoisonMessageException,ProcessedMessage,ProcessedMessageFactory,ProcessingOutcome}.cs` (new) | wire → domain and back |
| `…/Application/Pipeline/{IProfileStore,IPipelineHooks,IRawMessageHandler,ProfilePipeline}.cs` (new) | per-message orchestration |
| `…/Application/Retry/{RetryOptions,MessageRetryPolicy}.cs` (new) | 3 attempts, then poison |
| `…/Infrastructure/Persistence/{ProfileConfiguration,UserIdentityConfiguration,EventRecordConfiguration}.cs`, `ProfilesDbContext.cs` (modify), `ProfilesDbContextFactory.cs`, `Migrations/*` (new) | mapping and schema |
| `…/Infrastructure/Persistence/{EfProfileStore,SqlErrors}.cs`, `Caching/{IIdentityCache,RedisIdentityCache}.cs` (new) | store port implementation |
| `…/Infrastructure/Consumers/{ProfilesConsumerOptions,RawEventsConsumerService,ScopedMessageHandler,SchemaGate}.cs` (new) | Kafka consumer |
| `…/Infrastructure/Kafka/{WorkerProducer,ProcessedEventPublisher,DeadLetterPublisher,DeadLetterMessageFactory,IProcessedPublisher,IDeadLetterPublisher}.cs` (new) | producers |
| `…/Infrastructure/{ProfilesMetrics,ProfilesWorkerRegistration}.cs`, `Maintenance/{PartitionBoundaries,EventPartitionMaintenanceService}.cs` (new), `ProfilesModule.cs` (modify) | meter, partitions, DI |
| `src/Hosts/Crm.Worker/{Program.cs,StartupProbeService.cs,appsettings.json}` (modify) | registration only |
| `tests/Profiles.UnitTests/*.cs` (new) | UT-009…UT-015, pipeline, decoder, retry, DLQ headers, boundaries |
| `tests/Crm.IntegrationTests/Fixtures/{CrmWorkerFactory,SwitchableHooks,TopicReader,ProfilesDb,Eventually,KafkaTestClient,WorkerMetrics,RawEnvelopes}.cs` (new), `CrmStackFixture.cs` (modify) | shared Worker, helpers |
| `tests/Crm.IntegrationTests/Profiles/{SchemaTests,ProfileStoreTests,MergeTests,MergeConcurrencyTests,PartitionMaintenanceTests}.cs`, `Ingestion/{TrackPipelineTests,IdentifyTests,IdempotencyTests,DeadLetterTests,CommitOrderingTests}.cs` (new) | integration cases |
| `Makefile`, `docs/**` (modify) | `make migrate` covers Profiles; documentation |

---

### Task 1: Domain model and rules (UT-009 … UT-015)

**Files:**
- Create: `src/Modules/Profiles/Profiles.Domain/IdentityKinds.cs`, `IdentityKey.cs`, `EventTime.cs`, `ProfileAttributes.cs`, `Profile.cs`, `UserIdentity.cs`, `EventRecord.cs`, `MergePolicy.cs`, `ProfileMerge.cs`, `WinnerResolver.cs`, `MergeChainCorruptException.cs`
- Test: `tests/Profiles.UnitTests/IdentityNormalizationTests.cs`, `EventTimeTests.cs`, `ApplyTraitsTests.cs`, `ApplyEventTests.cs`, `MergePolicyTests.cs`, `ProfileMergeTests.cs`, `ResolveWinnerTests.cs`

**Interfaces:**
- Consumes: `Crm.Kernel.Entity` (`Id` protected set, `TenantId`/`CreatedAt`/`UpdatedAt` `protected internal` set).
- Produces (namespace `Crm.Profiles.Domain`):
  - `static class IdentityKinds { ExternalId = "external_id", AnonymousId = "anonymous_id", Email = "email", Phone = "phone" }`
  - `readonly record struct IdentityKey(string Kind, string Value)` with `static IdentityKey ExternalId(string)`, `static IdentityKey AnonymousId(string)`, `static IdentityKey? Email(string?)`, `static IdentityKey? Phone(string?)`
  - `static class EventTime { static DateTime Normalize(DateTime occurredAt, DateTime receivedAt); static DateTime TruncateToMilliseconds(DateTime) }`
  - `static class ProfileAttributes { const string Empty = "{}"; static AttributeChange ApplyTraits(string attributesJson, JsonElement traits); static AttributeChange MergeLoser(string winnerJson, string loserJson) }`, `sealed record AttributeChange(string Json, IReadOnlyList<string> ChangedKeys)`
  - `sealed class Profile : Entity` — `static Profile Create(Guid tenantId, DateTime seenAt)`, properties `ExternalId, Email, Phone, Attributes, FirstSeenAt, LastSeenAt, EventCount, MergedIntoId`, methods `ApplyTrack(DateTime receivedAt)`, `IReadOnlyList<string> ApplyIdentify(JsonElement traits, DateTime receivedAt)`, `AssignExternalId(string)`, `AssignEmail(string)`, `AssignPhone(string)`, `SetEventCount(long)`
  - `sealed class UserIdentity : Entity` — ctor `(Guid tenantId, IdentityKey key, Guid profileId)`, `Kind`, `Value`, `ProfileId`, `Key`, `RepointTo(Guid)`
  - `sealed class EventRecord : Entity` — ctor `(Guid id, Guid tenantId, Guid profileId, string name, string propertiesJson, DateTime occurredAt, DateTime receivedAt)`
  - `static class MergePolicy { static (Profile Winner, Profile Loser) Decide(Profile a, Profile b) }`
  - `sealed record ProfileMergedEvent(Guid WinnerId, Guid LoserId, IReadOnlyList<string> GainedKeys)`; `static class ProfileMerge { static ProfileMergedEvent Apply(Profile winner, Profile loser, IEnumerable<UserIdentity> loserIdentities) }`
  - `static class WinnerResolver { const int MaxHops = 16; static Task<Profile> ResolveAsync(Profile start, Func<Guid, CancellationToken, Task<Profile?>> load, CancellationToken ct) }`
  - `sealed class MergeChainCorruptException : Exception`

- [ ] **Step 1: Write the failing tests**

`tests/Profiles.UnitTests/IdentityNormalizationTests.cs`:

```csharp
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class IdentityNormalizationTests
{
    [Fact]
    public void UT009_identity_values_are_normalized()
    {
        IdentityKey.Email(" A@B.com ").Should().Be(new IdentityKey("email", "a@b.com"));
        IdentityKey.Phone("+62 812-3456").Should().Be(new IdentityKey("phone", "+628123456"));
        IdentityKey.ExternalId("U-1").Should().Be(new IdentityKey("external_id", "U-1"));
        IdentityKey.AnonymousId("Anon-9").Should().Be(new IdentityKey("anonymous_id", "Anon-9"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("@b.com")]
    [InlineData("a@")]
    [InlineData("a@b@c.com")]
    public void UT009_strings_that_are_not_an_email_yield_no_identity(string? raw)
        => IdentityKey.Email(raw).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("08123456789")]      // no country code
    [InlineData("+0123456789")]      // country codes never start with 0
    [InlineData("+62")]              // too short
    [InlineData("+6281234567890123456")] // longer than E.164 allows
    [InlineData("+62 81a 234")]
    public void UT009_strings_that_are_not_e164_yield_no_identity(string? raw)
        => IdentityKey.Phone(raw).Should().BeNull();

    [Fact]
    public void UT009_phone_punctuation_is_removed()
        => IdentityKey.Phone("+1 (415) 555.0101").Should().Be(new IdentityKey("phone", "+14155550101"));

    [Fact]
    public void UT009_email_longer_than_the_column_is_rejected()
        => IdentityKey.Email(new string('a', 250) + "@b.com").Should().BeNull();
}
```

`tests/Profiles.UnitTests/EventTimeTests.cs`:

```csharp
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class EventTimeTests
{
    private static readonly DateTime Received = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Occurred_at_is_truncated_to_milliseconds_so_it_round_trips_through_datetime2_3()
    {
        var occurred = Received.AddTicks(1_234_567); // 123.4567 ms

        var normalized = EventTime.Normalize(occurred, Received);

        normalized.Should().Be(Received.AddMilliseconds(123));
        normalized.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Occurred_at_is_clamped_to_seven_days_around_received_at()
    {
        EventTime.Normalize(Received.AddDays(-30), Received).Should().Be(Received.AddDays(-7));
        EventTime.Normalize(Received.AddDays(30), Received).Should().Be(Received.AddDays(7));
        EventTime.Normalize(Received.AddDays(-7), Received).Should().Be(Received.AddDays(-7));
    }

    [Fact]
    public void Normalizing_twice_is_the_same_as_once_so_redelivery_keeps_the_idempotency_key()
    {
        var once = EventTime.Normalize(Received.AddDays(-30).AddTicks(7), Received);
        EventTime.Normalize(once, Received).Should().Be(once);
    }
}
```

`tests/Profiles.UnitTests/ApplyTraitsTests.cs`:

```csharp
using System.Text.Json;
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ApplyTraitsTests
{
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Fact]
    public void UT015_traits_apply_per_key_last_write_wins()
    {
        var change = ProfileAttributes.ApplyTraits("""{"plan":"free","city":"SG"}""", Json("""{"plan":"pro","country":"ID"}"""));

        Json(change.Json).GetProperty("plan").GetString().Should().Be("pro");
        Json(change.Json).GetProperty("city").GetString().Should().Be("SG");
        Json(change.Json).GetProperty("country").GetString().Should().Be("ID");
        change.ChangedKeys.Should().Equal("plan", "country");
    }

    [Fact]
    public void UT015_an_equal_value_is_not_a_change()
    {
        var change = ProfileAttributes.ApplyTraits("""{"plan":"pro","age":30}""", Json("""{"plan":"pro","age":30}"""));

        change.ChangedKeys.Should().BeEmpty();
    }

    [Fact]
    public void UT015_a_null_trait_removes_the_key()
    {
        var change = ProfileAttributes.ApplyTraits("""{"plan":"pro","city":"SG"}""", Json("""{"city":null,"ghost":null}"""));

        Json(change.Json).TryGetProperty("city", out _).Should().BeFalse();
        change.ChangedKeys.Should().Equal("city");
    }

    [Fact]
    public void UT015_nested_values_are_replaced_whole()
    {
        var change = ProfileAttributes.ApplyTraits("""{"prefs":{"a":1,"b":2}}""", Json("""{"prefs":{"a":1}}"""));

        Json(change.Json).GetProperty("prefs").TryGetProperty("b", out _).Should().BeFalse();
        change.ChangedKeys.Should().Equal("prefs");
    }

    [Fact]
    public void UT015_traits_that_are_not_an_object_change_nothing()
    {
        ProfileAttributes.ApplyTraits("""{"plan":"pro"}""", default).ChangedKeys.Should().BeEmpty();
        ProfileAttributes.ApplyTraits("""{"plan":"pro"}""", Json("[1,2]")).Json.Should().Be("""{"plan":"pro"}""");
    }

    [Fact]
    public void UT011_merge_keeps_winner_attributes_on_conflict()
    {
        var change = ProfileAttributes.MergeLoser("""{"plan":"pro","city":"SG"}""", """{"plan":"free","country":"ID"}""");

        Json(change.Json).GetProperty("plan").GetString().Should().Be("pro");
        Json(change.Json).GetProperty("city").GetString().Should().Be("SG");
        Json(change.Json).GetProperty("country").GetString().Should().Be("ID");
        change.ChangedKeys.Should().Equal("country");
    }
}
```

`tests/Profiles.UnitTests/ApplyEventTests.cs`:

```csharp
using System.Text.Json;
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ApplyEventTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UT014_track_and_identify_update_different_counters()
    {
        var profile = Profile.Create(Tenant, T0);
        profile.SetEventCount(5);
        using var traits = JsonDocument.Parse("""{"plan":"pro"}""");

        profile.ApplyTrack(T0.AddMinutes(1));
        profile.EventCount.Should().Be(6);
        profile.LastSeenAt.Should().Be(T0.AddMinutes(1));

        profile.ApplyIdentify(traits.RootElement, T0.AddMinutes(2));
        profile.EventCount.Should().Be(6);
        profile.LastSeenAt.Should().Be(T0.AddMinutes(2));
    }

    [Fact]
    public void UT014_last_seen_never_moves_backwards()
    {
        var profile = Profile.Create(Tenant, T0);

        profile.ApplyTrack(T0.AddMinutes(-5));

        profile.LastSeenAt.Should().Be(T0);
        profile.FirstSeenAt.Should().Be(T0);
        profile.EventCount.Should().Be(1);
    }

    [Fact]
    public void A_new_profile_is_empty_and_belongs_to_the_tenant()
    {
        var profile = Profile.Create(Tenant, T0);

        profile.TenantId.Should().Be(Tenant);
        profile.Attributes.Should().Be("{}");
        profile.EventCount.Should().Be(0);
        profile.MergedIntoId.Should().BeNull();
        profile.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void External_id_can_be_assigned_once()
    {
        var profile = Profile.Create(Tenant, T0);
        profile.AssignExternalId("u1");

        profile.AssignExternalId("u1");
        var act = () => profile.AssignExternalId("u2");

        profile.ExternalId.Should().Be("u1");
        act.Should().Throw<InvalidOperationException>();
    }
}
```

`tests/Profiles.UnitTests/MergePolicyTests.cs`:

```csharp
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class MergePolicyTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime TenOClock = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UT010_merge_winner_is_the_older_profile()
    {
        var p1 = Profile.Create(Tenant, TenOClock);
        var p2 = Profile.Create(Tenant, TenOClock.AddHours(1));

        MergePolicy.Decide(p1, p2).Should().Be((p1, p2));
        MergePolicy.Decide(p2, p1).Should().Be((p1, p2));
    }

    [Fact]
    public void UT010_equal_first_seen_falls_back_to_the_smaller_id_so_the_choice_is_stable()
    {
        var a = Profile.Create(Tenant, TenOClock);
        var b = Profile.Create(Tenant, TenOClock);
        var expectedWinner = a.Id.CompareTo(b.Id) < 0 ? a : b;

        MergePolicy.Decide(a, b).Winner.Should().BeSameAs(expectedWinner);
        MergePolicy.Decide(b, a).Winner.Should().BeSameAs(expectedWinner);
    }
}
```

`tests/Profiles.UnitTests/ProfileMergeTests.cs`:

```csharp
using System.Text.Json;
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ProfileMergeTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UT012_merge_repoints_identities_and_marks_the_loser()
    {
        var winner = Profile.Create(Tenant, T0);
        var loser = Profile.Create(Tenant, T0.AddHours(1));
        var identities = new[]
        {
            new UserIdentity(Tenant, IdentityKey.AnonymousId("a1"), loser.Id),
            new UserIdentity(Tenant, IdentityKey.Email("x@y.z")!.Value, loser.Id),
        };

        var merged = ProfileMerge.Apply(winner, loser, identities);

        identities.Should().OnlyContain(i => i.ProfileId == winner.Id);
        loser.MergedIntoId.Should().Be(winner.Id);
        merged.WinnerId.Should().Be(winner.Id);
        merged.LoserId.Should().Be(loser.Id);
    }

    [Fact]
    public void UT012_the_winner_adopts_contact_points_it_lacks_and_the_loser_gives_them_up()
    {
        var winner = Profile.Create(Tenant, T0);
        var loser = Profile.Create(Tenant, T0.AddHours(1));
        loser.AssignExternalId("u1");
        loser.AssignEmail("x@y.z");
        winner.AssignPhone("+628123456");
        loser.AssignPhone("+14155550101");

        ProfileMerge.Apply(winner, loser, []);

        winner.ExternalId.Should().Be("u1");
        winner.Email.Should().Be("x@y.z");
        winner.Phone.Should().Be("+628123456", "the winner's own value wins");
        loser.ExternalId.Should().BeNull("the unique index allows one owner per value");
        loser.Email.Should().BeNull();
        loser.Phone.Should().BeNull();
    }

    [Fact]
    public void UT012_attributes_merge_and_last_seen_is_the_later_of_the_two()
    {
        var winner = Profile.Create(Tenant, T0);
        var loser = Profile.Create(Tenant, T0.AddHours(1));
        using var winnerTraits = JsonDocument.Parse("""{"plan":"pro"}""");
        using var loserTraits = JsonDocument.Parse("""{"plan":"free","country":"ID"}""");
        winner.ApplyIdentify(winnerTraits.RootElement, T0);
        loser.ApplyIdentify(loserTraits.RootElement, T0.AddHours(2));

        var merged = ProfileMerge.Apply(winner, loser, []);

        merged.GainedKeys.Should().Equal("country");
        winner.Attributes.Should().Contain("\"plan\":\"pro\"").And.Contain("\"country\":\"ID\"");
        winner.LastSeenAt.Should().Be(T0.AddHours(2));
        winner.FirstSeenAt.Should().Be(T0);
    }

    [Fact]
    public void A_profile_cannot_be_merged_into_itself_or_twice()
    {
        var winner = Profile.Create(Tenant, T0);
        var loser = Profile.Create(Tenant, T0.AddHours(1));
        ProfileMerge.Apply(winner, loser, []);

        var again = () => ProfileMerge.Apply(winner, loser, []);
        var self = () => ProfileMerge.Apply(winner, winner, []);

        again.Should().Throw<InvalidOperationException>();
        self.Should().Throw<InvalidOperationException>();
    }
}
```

`tests/Profiles.UnitTests/ResolveWinnerTests.cs`:

```csharp
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ResolveWinnerTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task UT013_applier_follows_MergedIntoId_to_the_winner()
    {
        var c = Profile.Create(Tenant, T0);
        var b = Profile.Create(Tenant, T0.AddHours(1));
        var a = Profile.Create(Tenant, T0.AddHours(2));
        ProfileMerge.Apply(b, a, []); // a -> b (both live at this point)
        ProfileMerge.Apply(c, b, []); // b -> c, so a -> b -> c
        var byId = new Dictionary<Guid, Profile> { [a.Id] = a, [b.Id] = b, [c.Id] = c };

        var winner = await WinnerResolver.ResolveAsync(a, (id, _) => Task.FromResult(byId.GetValueOrDefault(id)), CancellationToken.None);

        winner.Should().BeSameAs(c);
    }

    [Fact]
    public async Task UT013_a_profile_that_was_never_merged_is_its_own_winner()
    {
        var a = Profile.Create(Tenant, T0);

        var winner = await WinnerResolver.ResolveAsync(a, (_, _) => throw new InvalidOperationException("must not load"), CancellationToken.None);

        winner.Should().BeSameAs(a);
    }

    [Fact]
    public async Task UT013_a_cycle_throws_MergeChainCorruptException()
    {
        var a = Profile.Create(Tenant, T0);
        var b = Profile.Create(Tenant, T0.AddHours(1));
        ProfileMerge.Apply(a, b, []);          // b -> a
        ForceMergedInto(a, b.Id);              // a -> b, which the aggregate itself refuses to do
        var byId = new Dictionary<Guid, Profile> { [a.Id] = a, [b.Id] = b };

        var act = () => WinnerResolver.ResolveAsync(a, (id, _) => Task.FromResult(byId.GetValueOrDefault(id)), CancellationToken.None);

        await act.Should().ThrowAsync<MergeChainCorruptException>();
    }

    [Fact]
    public async Task UT013_a_dangling_pointer_throws_MergeChainCorruptException()
    {
        var winner = Profile.Create(Tenant, T0);
        var loser = Profile.Create(Tenant, T0.AddHours(1));
        ProfileMerge.Apply(winner, loser, []);

        var act = () => WinnerResolver.ResolveAsync(loser, (_, _) => Task.FromResult<Profile?>(null), CancellationToken.None);

        await act.Should().ThrowAsync<MergeChainCorruptException>();
    }

    /// <summary>Corrupt data can only come from outside the aggregate, so the test writes the property the way a bad row would.</summary>
    private static void ForceMergedInto(Profile profile, Guid target)
        => typeof(Profile).GetProperty(nameof(Profile.MergedIntoId))!.SetValue(profile, target);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Profiles.UnitTests`
Expected: build FAILS — `IdentityKey`, `Profile`, … do not exist.

- [ ] **Step 3: Implement the domain**

`IdentityKinds.cs`:

```csharp
namespace Crm.Profiles.Domain;

/// <summary>The four identity kinds of FR-08; the strings are the values of profiles.UserIdentities.Kind.</summary>
public static class IdentityKinds
{
    public const string ExternalId = "external_id";
    public const string AnonymousId = "anonymous_id";
    public const string Email = "email";
    public const string Phone = "phone";
}
```

`IdentityKey.cs`:

```csharp
using System.Text;

namespace Crm.Profiles.Domain;

/// <summary>A normalized (kind, value) pair. Exactly one profile per key per tenant (FR-08).</summary>
public readonly record struct IdentityKey(string Kind, string Value)
{
    private const int MaxEmailLength = 256;   // profiles.Profiles.Email
    private const int MinPhoneDigits = 7;
    private const int MaxPhoneDigits = 15;    // E.164

    /// <summary>Customer ids are opaque: case and whitespace are preserved exactly as the API accepted them.</summary>
    public static IdentityKey ExternalId(string value) => new(IdentityKinds.ExternalId, value);

    public static IdentityKey AnonymousId(string value) => new(IdentityKinds.AnonymousId, value);

    /// <summary>Trimmed and lower-cased; null when the text is not shaped like one address.</summary>
    public static IdentityKey? Email(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim().ToLowerInvariant();
        var at = value.IndexOf('@', StringComparison.Ordinal);
        var isAddress = at > 0 && at < value.Length - 1 && value.IndexOf('@', at + 1) < 0 && !value.Any(char.IsWhiteSpace);
        return isAddress && value.Length <= MaxEmailLength ? new IdentityKey(IdentityKinds.Email, value) : null;
    }

    /// <summary>E.164: '+', a non-zero digit, 7 to 15 digits in total. Spaces, dashes, dots and parentheses are dropped; anything else disqualifies.</summary>
    public static IdentityKey? Phone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed[0] != '+')
        {
            return null;
        }

        var digits = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed.AsSpan(1))
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
            }
            else if (c is not (' ' or '-' or '.' or '(' or ')'))
            {
                return null;
            }
        }

        var valid = digits.Length is >= MinPhoneDigits and <= MaxPhoneDigits && digits[0] != '0';
        return valid ? new IdentityKey(IdentityKinds.Phone, "+" + digits) : null;
    }
}
```

`EventTime.cs`:

```csharp
namespace Crm.Profiles.Domain;

public static class EventTime
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>
    /// profiles.Events.OccurredAt is datetime2(3) and half of the idempotency key (Id, OccurredAt): the value is
    /// truncated to milliseconds and clamped to ReceivedAt ± 7 d before anything reads it, so a redelivered message
    /// computes exactly the value that is already stored.
    /// </summary>
    public static DateTime Normalize(DateTime occurredAt, DateTime receivedAt)
    {
        var received = TruncateToMilliseconds(receivedAt);
        var occurred = TruncateToMilliseconds(occurredAt);
        if (occurred < received - Window)
        {
            return received - Window;
        }

        return occurred > received + Window ? received + Window : occurred;
    }

    public static DateTime TruncateToMilliseconds(DateTime value)
        => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
}
```

`ProfileAttributes.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Crm.Profiles.Domain;

public sealed record AttributeChange(string Json, IReadOnlyList<string> ChangedKeys);

/// <summary>The Attributes JSON document of a profile. Keys are top-level; a value is replaced whole.</summary>
public static class ProfileAttributes
{
    public const string Empty = "{}";

    /// <summary>FR-10: per key, last write wins. A JSON null removes the key. Keys are reported in the order the traits list them.</summary>
    public static AttributeChange ApplyTraits(string attributesJson, JsonElement traits)
    {
        if (traits.ValueKind != JsonValueKind.Object)
        {
            return new AttributeChange(attributesJson, []);
        }

        var attributes = Parse(attributesJson);
        var changed = new List<string>();
        foreach (var trait in traits.EnumerateObject())
        {
            var exists = attributes.TryGetPropertyValue(trait.Name, out var current);
            if (trait.Value.ValueKind == JsonValueKind.Null)
            {
                if (exists)
                {
                    attributes.Remove(trait.Name);
                    changed.Add(trait.Name);
                }

                continue;
            }

            var incoming = JsonNode.Parse(trait.Value.GetRawText());
            if (exists && JsonNode.DeepEquals(current, incoming))
            {
                continue;
            }

            attributes[trait.Name] = incoming;
            changed.Add(trait.Name);
        }

        return changed.Count == 0 ? new AttributeChange(attributesJson, []) : new AttributeChange(attributes.ToJsonString(), changed);
    }

    /// <summary>FR-09: the loser only contributes keys the winner does not have.</summary>
    public static AttributeChange MergeLoser(string winnerJson, string loserJson)
    {
        var winner = Parse(winnerJson);
        var gained = new List<string>();
        foreach (var (key, value) in Parse(loserJson))
        {
            if (winner.ContainsKey(key))
            {
                continue;
            }

            winner[key] = value?.DeepClone();
            gained.Add(key);
        }

        return gained.Count == 0 ? new AttributeChange(winnerJson, []) : new AttributeChange(winner.ToJsonString(), gained);
    }

    private static JsonObject Parse(string json)
        => JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? Empty : json) as JsonObject
           ?? throw new InvalidOperationException("Profile attributes must be a JSON object.");
}
```

`Profile.cs`:

```csharp
using System.Text.Json;
using Crm.Kernel;

namespace Crm.Profiles.Domain;

/// <summary>One unified end user of a tenant (profiles.Profiles). Mutated only while its row is locked.</summary>
public sealed class Profile : Entity
{
    private Profile()
    {
    }

    public string? ExternalId { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public string Attributes { get; private set; } = ProfileAttributes.Empty;

    public DateTime FirstSeenAt { get; private set; }

    public DateTime LastSeenAt { get; private set; }

    public long EventCount { get; private set; }

    /// <summary>Non-null on a merge loser; such rows are excluded from every read and only redirect to the winner.</summary>
    public Guid? MergedIntoId { get; private set; }

    public static Profile Create(Guid tenantId, DateTime seenAt)
    {
        var at = EventTime.TruncateToMilliseconds(seenAt);
        return new Profile { TenantId = tenantId, FirstSeenAt = at, LastSeenAt = at };
    }

    public void ApplyTrack(DateTime receivedAt)
    {
        EventCount++;
        Touch(receivedAt);
    }

    /// <summary>identify never changes EventCount (FR-10). Returns the keys whose value changed.</summary>
    public IReadOnlyList<string> ApplyIdentify(JsonElement traits, DateTime receivedAt)
    {
        var change = ProfileAttributes.ApplyTraits(Attributes, traits);
        Attributes = change.Json;
        Touch(receivedAt);
        return change.ChangedKeys;
    }

    public void AssignExternalId(string externalId)
    {
        if (ExternalId is not null && !string.Equals(ExternalId, externalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Profile {Id} already belongs to external id '{ExternalId}'.");
        }

        ExternalId = externalId;
    }

    public void AssignEmail(string normalizedEmail) => Email = normalizedEmail;

    public void AssignPhone(string normalizedPhone) => Phone = normalizedPhone;

    public void SetEventCount(long count) => EventCount = count;

    internal IReadOnlyList<string> Absorb(Profile loser)
    {
        var change = ProfileAttributes.MergeLoser(Attributes, loser.Attributes);
        Attributes = change.Json;
        ExternalId ??= loser.ExternalId;
        Email ??= loser.Email;
        Phone ??= loser.Phone;
        FirstSeenAt = loser.FirstSeenAt < FirstSeenAt ? loser.FirstSeenAt : FirstSeenAt;
        LastSeenAt = loser.LastSeenAt > LastSeenAt ? loser.LastSeenAt : LastSeenAt;
        return change.ChangedKeys;
    }

    internal void MarkMergedInto(Guid winnerId)
    {
        ExternalId = null;
        Email = null;
        Phone = null;
        MergedIntoId = winnerId;
    }

    private void Touch(DateTime receivedAt)
    {
        var at = EventTime.TruncateToMilliseconds(receivedAt);
        if (at > LastSeenAt)
        {
            LastSeenAt = at;
        }
    }
}
```

`UserIdentity.cs`:

```csharp
using Crm.Kernel;

namespace Crm.Profiles.Domain;

/// <summary>
/// profiles.UserIdentities: primary key (TenantId, Kind, Value), so TenantId is set here rather than left to the
/// save interceptor (which still verifies it). Entity.Id is not mapped for this type.
/// </summary>
public sealed class UserIdentity : Entity
{
    private UserIdentity()
    {
    }

    public UserIdentity(Guid tenantId, IdentityKey key, Guid profileId)
    {
        TenantId = tenantId;
        Kind = key.Kind;
        Value = key.Value;
        ProfileId = profileId;
    }

    public string Kind { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public Guid ProfileId { get; private set; }

    public IdentityKey Key => new(Kind, Value);

    public void RepointTo(Guid profileId) => ProfileId = profileId;
}
```

`EventRecord.cs`:

```csharp
using Crm.Kernel;

namespace Crm.Profiles.Domain;

/// <summary>profiles.Events: one row per track call. Id is the API's UUIDv7; (Id, OccurredAt) is the idempotency key.</summary>
public sealed class EventRecord : Entity
{
    private EventRecord()
    {
    }

    public EventRecord(Guid id, Guid tenantId, Guid profileId, string name, string propertiesJson, DateTime occurredAt, DateTime receivedAt)
    {
        Id = id;
        TenantId = tenantId;
        ProfileId = profileId;
        Name = name;
        Properties = propertiesJson;
        ReceivedAt = EventTime.TruncateToMilliseconds(receivedAt);
        OccurredAt = EventTime.Normalize(occurredAt, receivedAt);
    }

    public Guid ProfileId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Properties { get; private set; } = ProfileAttributes.Empty;

    public DateTime OccurredAt { get; private set; }

    public DateTime ReceivedAt { get; private set; }
}
```

`MergePolicy.cs`:

```csharp
namespace Crm.Profiles.Domain;

public static class MergePolicy
{
    /// <summary>FR-09: the older profile wins. Equal FirstSeenAt falls back to the smaller id so both argument orders agree.</summary>
    public static (Profile Winner, Profile Loser) Decide(Profile a, Profile b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var byAge = a.FirstSeenAt.CompareTo(b.FirstSeenAt);
        var aWins = byAge < 0 || (byAge == 0 && a.Id.CompareTo(b.Id) <= 0);
        return aWins ? (a, b) : (b, a);
    }
}
```

`ProfileMerge.cs`:

```csharp
namespace Crm.Profiles.Domain;

public sealed record ProfileMergedEvent(Guid WinnerId, Guid LoserId, IReadOnlyList<string> GainedKeys);

/// <summary>
/// The in-memory half of a merge. The caller holds UPDLOCK, HOLDLOCK on both rows, re-points profiles.Events with
/// one UPDATE and recomputes the winner's EventCount afterwards (spec §4.2 step 2–3).
/// </summary>
public static class ProfileMerge
{
    public static ProfileMergedEvent Apply(Profile winner, Profile loser, IEnumerable<UserIdentity> loserIdentities)
    {
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(loser);
        ArgumentNullException.ThrowIfNull(loserIdentities);

        if (winner.Id == loser.Id)
        {
            throw new InvalidOperationException($"Profile {winner.Id} cannot be merged into itself.");
        }

        if (winner.MergedIntoId is not null || loser.MergedIntoId is not null)
        {
            throw new InvalidOperationException("Both profiles must be live; resolve merge chains before merging.");
        }

        var gained = winner.Absorb(loser);
        foreach (var identity in loserIdentities)
        {
            identity.RepointTo(winner.Id);
        }

        loser.MarkMergedInto(winner.Id);
        return new ProfileMergedEvent(winner.Id, loser.Id, gained);
    }
}
```

`MergeChainCorruptException.cs`:

```csharp
namespace Crm.Profiles.Domain;

/// <summary>A MergedIntoId chain loops, dangles or is implausibly long. Data corruption: the message is dead-lettered.</summary>
public sealed class MergeChainCorruptException : Exception
{
    public MergeChainCorruptException()
    {
    }

    public MergeChainCorruptException(string message)
        : base(message)
    {
    }

    public MergeChainCorruptException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

`WinnerResolver.cs`:

```csharp
namespace Crm.Profiles.Domain;

public static class WinnerResolver
{
    public const int MaxHops = 16;

    /// <summary>
    /// Follows MergedIntoId until a live profile is reached. <paramref name="load"/> is expected to lock the row it
    /// returns, so an event that raced a merge is applied to the winner (spec §4.2, semantics item 2).
    /// </summary>
    public static async Task<Profile> ResolveAsync(Profile start, Func<Guid, CancellationToken, Task<Profile?>> load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(load);

        var visited = new HashSet<Guid> { start.Id };
        var current = start;
        while (current.MergedIntoId is { } next)
        {
            if (!visited.Add(next) || visited.Count > MaxHops)
            {
                throw new MergeChainCorruptException($"Merge chain starting at profile {start.Id} loops or exceeds {MaxHops} hops.");
            }

            current = await load(next, cancellationToken).ConfigureAwait(false)
                ?? throw new MergeChainCorruptException($"Profile {current.Id} points to missing profile {next}.");
        }

        return current;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Profiles.UnitTests`
Expected: PASS, 0 warnings. Then `dotnet build Crm.sln -warnaserror` → 0 warnings and `dotnet test tests/Crm.ArchTests` → all green (no mapping exists yet, so AT-006 still iterates an empty Profiles model).

If `ConfigureAwait` is flagged either way by an analyzer, follow what `src/Modules/Tenancy` does and say so in the report.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Profiles/Profiles.Domain tests/Profiles.UnitTests
git commit -m "feat(profiles): domain rules for identities, traits, merge and merge chains (UT-009..UT-015)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Schema `profiles` — mapping, partitioned migration, `make migrate`

**Files:**
- Create: `src/Modules/Profiles/Profiles.Infrastructure/Persistence/PromotedAttributes.cs`, `ProfileConfiguration.cs`, `UserIdentityConfiguration.cs`, `EventRecordConfiguration.cs`, `src/Modules/Profiles/Profiles.Infrastructure/ProfilesDbContextFactory.cs`, `src/Modules/Profiles/Profiles.Infrastructure/Migrations/*` (scaffolded, then edited)
- Modify: `src/Modules/Profiles/Profiles.Infrastructure/ProfilesDbContext.cs`, `ProfilesModule.cs`, `Profiles.Infrastructure.csproj`, `Makefile` (`migrate`, `migrate-run`), `tests/Crm.IntegrationTests/Fixtures/CrmStackFixture.cs`
- Test: `tests/Crm.IntegrationTests/Profiles/SchemaTests.cs`

**Interfaces:**
- Consumes: Task 1's `Profile`, `UserIdentity`, `EventRecord`; `Crm.Kernel.Data.ApplyTenantFilters`, `TenantSaveChangesInterceptor`.
- Produces: `ProfilesDbContext.Profiles`, `.UserIdentities`, `.Events` (`DbSet<>`), `ProfilesDbContext.MigrationsHistoryTable = "__EFMigrationsHistory_profiles"`; database objects exactly as `docs/data-dictionary/profiles.md` names them; `CrmStackFixture` migrates the `profiles` schema before any test runs.

- [ ] **Step 1: Write the failing schema test**

`tests/Crm.IntegrationTests/Profiles/SchemaTests.cs`:

```csharp
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

/// <summary>The migration must produce what docs/data-dictionary/profiles.md names; EF cannot express partitioning, so it is checked in the catalog.</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public class SchemaTests(CrmStackFixture stack)
{
    [Fact]
    public async Task Events_and_all_of_its_indexes_live_on_the_monthly_partition_scheme()
    {
        var rows = await QueryAsync(
            """
            SELECT i.name + '|' + ds.name + '|' + CAST(i.is_unique AS char(1))
            FROM sys.indexes i
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.object_id = OBJECT_ID(N'profiles.Events') AND i.name IS NOT NULL
            ORDER BY i.name
            """);

        rows.Should().Equal(
            "CX_Events_TenantId_ProfileId_OccurredAt|PS_Events_Monthly|0",
            "IX_Events_TenantId_Name_OccurredAt|PS_Events_Monthly|0",
            "UX_Events_Id|PS_Events_Monthly|1");
    }

    [Fact]
    public async Task Partition_function_is_monthly_range_right_with_at_least_two_years_of_boundaries()
    {
        var rows = await QueryAsync(
            """
            SELECT CAST(f.boundary_value_on_right AS char(1)) + '|' + CAST(COUNT(v.boundary_id) AS varchar(10))
            FROM sys.partition_functions f
            JOIN sys.partition_range_values v ON v.function_id = f.function_id
            WHERE f.name = N'PF_Events_Monthly'
            GROUP BY f.boundary_value_on_right
            """);

        rows.Should().ContainSingle();
        var parts = rows[0].Split('|');
        parts[0].Should().Be("1");
        int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(24);
    }

    [Fact]
    public async Task Profiles_has_the_documented_indexes_constraints_and_promoted_columns()
    {
        var indexes = await QueryAsync(
            """
            SELECT i.name + '|' + i.type_desc + '|' + CAST(i.is_unique AS char(1)) + '|' + CAST(i.has_filter AS char(1))
            FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'profiles.Profiles') ORDER BY i.name
            """);
        indexes.Should().Equal(
            "CX_Profiles_TenantId_Id|CLUSTERED|1|0",
            "IX_Profiles_TenantId_Attr_city|NONCLUSTERED|0|0",
            "IX_Profiles_TenantId_Attr_country|NONCLUSTERED|0|0",
            "IX_Profiles_TenantId_Attr_plan|NONCLUSTERED|0|0",
            "IX_Profiles_TenantId_LastSeenAt|NONCLUSTERED|0|0",
            "PK_Profiles|NONCLUSTERED|1|0",
            "UX_Profiles_TenantId_Email|NONCLUSTERED|1|1",
            "UX_Profiles_TenantId_ExternalId|NONCLUSTERED|1|1",
            "UX_Profiles_TenantId_Phone|NONCLUSTERED|1|1");

        var computed = await QueryAsync(
            "SELECT name + '|' + CAST(is_persisted AS char(1)) FROM sys.computed_columns WHERE object_id = OBJECT_ID(N'profiles.Profiles') ORDER BY name");
        computed.Should().Equal("Attr_city|1", "Attr_country|1", "Attr_plan|1");

        var constraints = await QueryAsync(
            """
            SELECT name FROM sys.check_constraints WHERE parent_object_id IN (OBJECT_ID(N'profiles.Profiles'), OBJECT_ID(N'profiles.Events'), OBJECT_ID(N'profiles.UserIdentities'))
            UNION ALL
            SELECT name FROM sys.foreign_keys WHERE parent_object_id IN (OBJECT_ID(N'profiles.Profiles'), OBJECT_ID(N'profiles.Events'), OBJECT_ID(N'profiles.UserIdentities'))
            ORDER BY name
            """);
        constraints.Should().Equal(
            "CK_Events_Properties_IsJson",
            "CK_Profiles_Attributes_IsJson",
            "CK_UserIdentities_Kind",
            "FK_Events_ProfileId",
            "FK_Profiles_MergedIntoId",
            "FK_UserIdentities_ProfileId");
    }

    [Fact]
    public async Task UserIdentities_is_keyed_by_tenant_kind_and_value()
    {
        var rows = await QueryAsync(
            """
            SELECT i.name + '|' + i.type_desc + '|' + STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(N'profiles.UserIdentities')
            GROUP BY i.name, i.type_desc ORDER BY i.name
            """);

        rows.Should().Equal(
            "IX_UserIdentities_ProfileId|NONCLUSTERED|ProfileId",
            "PK_UserIdentities|CLUSTERED|TenantId,Kind,Value");
    }

    [Fact]
    public async Task Migration_history_is_kept_per_module()
    {
        var rows = await QueryAsync("SELECT CAST(COUNT(*) AS varchar(10)) FROM profiles.__EFMigrationsHistory_profiles");
        int.Parse(rows[0], System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(1);
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
```

The catalog queries sort with the database collation; if an `Equal` assertion fails only on order, sort both sides with `StringComparer.Ordinal` rather than loosening the content check.

- [ ] **Step 2: Run it to verify it fails**

Run (environment prefix from Global Constraints): `dotnet test Crm.sln --filter "Category=Integration&FullyQualifiedName~Profiles.SchemaTests"`
Expected: FAIL — `Invalid object name 'profiles.__EFMigrationsHistory_profiles'` / empty index lists.

- [ ] **Step 3: Mapping**

`Profiles.Infrastructure.csproj` — add to the package `ItemGroup`:

```xml
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />
```

`Persistence/PromotedAttributes.cs`:

```csharp
namespace Crm.Profiles.Infrastructure.Persistence;

/// <summary>Slice 1 ships a fixed hot-key list (spec §3.1); each key gets a persisted computed column and an index.</summary>
internal static class PromotedAttributes
{
    public static readonly IReadOnlyList<string> Keys = ["plan", "country", "city"];

    public static string ColumnName(string key) => $"Attr_{key}";

    public static string ColumnSql(string key) => $"CAST(JSON_VALUE([Attributes], '$.{key}') AS nvarchar(256))";
}
```

`Persistence/ProfileConfiguration.cs`:

```csharp
using Crm.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Profiles.Infrastructure.Persistence;

/// <summary>profiles.Profiles. FK_Profiles_MergedIntoId is created by the migration's SQL, not modelled, so EF adds no index for it.</summary>
internal sealed class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> builder)
    {
        builder.ToTable("Profiles", t => t.HasCheckConstraint("CK_Profiles_Attributes_IsJson", "ISJSON([Attributes]) = 1"));

        builder.HasKey(p => p.Id).HasName("PK_Profiles").IsClustered(false);
        builder.HasIndex(p => new { p.TenantId, p.Id }).IsUnique().IsClustered().HasDatabaseName("CX_Profiles_TenantId_Id");

        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.ExternalId).HasMaxLength(128);
        builder.Property(p => p.Email).HasMaxLength(256);
        builder.Property(p => p.Phone).HasMaxLength(32);
        builder.Property(p => p.Attributes).HasColumnType("nvarchar(max)").HasDefaultValueSql("N'{}'");
        builder.Property(p => p.EventCount).HasDefaultValue(0L);
        builder.Property(p => p.FirstSeenAt).HasPrecision(3);
        builder.Property(p => p.LastSeenAt).HasPrecision(3);
        builder.Property(p => p.CreatedAt).HasPrecision(3);
        builder.Property(p => p.UpdatedAt).HasPrecision(3);

        builder.HasIndex(p => new { p.TenantId, p.ExternalId }).IsUnique().HasFilter("[ExternalId] IS NOT NULL").HasDatabaseName("UX_Profiles_TenantId_ExternalId");
        builder.HasIndex(p => new { p.TenantId, p.Email }).IsUnique().HasFilter("[Email] IS NOT NULL").HasDatabaseName("UX_Profiles_TenantId_Email");
        builder.HasIndex(p => new { p.TenantId, p.Phone }).IsUnique().HasFilter("[Phone] IS NOT NULL").HasDatabaseName("UX_Profiles_TenantId_Phone");
        builder.HasIndex(p => new { p.TenantId, p.LastSeenAt }).IsDescending(false, true).HasDatabaseName("IX_Profiles_TenantId_LastSeenAt");

        foreach (var key in PromotedAttributes.Keys)
        {
            var column = PromotedAttributes.ColumnName(key);
            builder.Property<string?>(column).HasMaxLength(256).HasComputedColumnSql(PromotedAttributes.ColumnSql(key), stored: true);
            builder.HasIndex(nameof(Profile.TenantId), column).HasDatabaseName($"IX_Profiles_TenantId_{column}");
        }
    }
}
```

`Persistence/UserIdentityConfiguration.cs`:

```csharp
using Crm.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Profiles.Infrastructure.Persistence;

internal sealed class UserIdentityConfiguration : IEntityTypeConfiguration<UserIdentity>
{
    public void Configure(EntityTypeBuilder<UserIdentity> builder)
    {
        builder.ToTable("UserIdentities", t => t.HasCheckConstraint("CK_UserIdentities_Kind", "[Kind] IN ('external_id','anonymous_id','email','phone')"));

        // The table has no surrogate id: the data dictionary keys it by (TenantId, Kind, Value).
        builder.Ignore(i => i.Id);
        builder.Ignore(i => i.Key);
        builder.HasKey(i => new { i.TenantId, i.Kind, i.Value }).HasName("PK_UserIdentities");

        builder.Property(i => i.TenantId).ValueGeneratedNever();
        builder.Property(i => i.Kind).HasMaxLength(16).IsUnicode(false);
        builder.Property(i => i.Value).HasMaxLength(256);
        builder.Property(i => i.CreatedAt).HasPrecision(3);
        builder.Property(i => i.UpdatedAt).HasPrecision(3);

        builder.HasOne<Profile>().WithMany().HasForeignKey(i => i.ProfileId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_UserIdentities_ProfileId");
        builder.HasIndex(i => i.ProfileId).HasDatabaseName("IX_UserIdentities_ProfileId");
    }
}
```

`Persistence/EventRecordConfiguration.cs`:

```csharp
using Crm.Profiles.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Profiles.Infrastructure.Persistence;

/// <summary>
/// profiles.Events, partitioned by month on OccurredAt. EF needs a key, the database needs a partition-aligned
/// unique index: the key is named UX_Events_Id and the migration creates it as that index instead of a PRIMARY KEY.
/// FK_Events_ProfileId is created by the migration's SQL so EF adds no IX_Events_ProfileId.
/// </summary>
internal sealed class EventRecordConfiguration : IEntityTypeConfiguration<EventRecord>
{
    public void Configure(EntityTypeBuilder<EventRecord> builder)
    {
        builder.ToTable("Events", t => t.HasCheckConstraint("CK_Events_Properties_IsJson", "ISJSON([Properties]) = 1"));

        builder.HasKey(e => new { e.Id, e.OccurredAt }).HasName("UX_Events_Id").IsClustered(false);
        builder.HasIndex(e => new { e.TenantId, e.ProfileId, e.OccurredAt }).IsClustered().HasDatabaseName("CX_Events_TenantId_ProfileId_OccurredAt");
        builder.HasIndex(e => new { e.TenantId, e.Name, e.OccurredAt }).IncludeProperties(e => e.ProfileId).HasDatabaseName("IX_Events_TenantId_Name_OccurredAt");

        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Name).HasMaxLength(64);
        builder.Property(e => e.Properties).HasColumnType("nvarchar(max)").HasDefaultValueSql("N'{}'");
        builder.Property(e => e.OccurredAt).HasPrecision(3);
        builder.Property(e => e.ReceivedAt).HasPrecision(3);
        builder.Property(e => e.CreatedAt).HasPrecision(3);
        builder.Property(e => e.UpdatedAt).HasPrecision(3);
    }
}
```

`ProfilesDbContext.cs` — replace the class body:

```csharp
using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Profiles.Domain;
using Crm.Profiles.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Crm.Profiles.Infrastructure;

public sealed class ProfilesDbContext(DbContextOptions<ProfilesDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "profiles";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_profiles";

    public Guid CurrentTenantId => tenant.TenantId;

    public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();

    public DbSet<EventRecord> Events => Set<EventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new ProfileConfiguration());
        modelBuilder.ApplyConfiguration(new UserIdentityConfiguration());
        modelBuilder.ApplyConfiguration(new EventRecordConfiguration());
        modelBuilder.ApplyTenantFilters(this);
    }
}
```

In `ProfilesModule.cs` replace the literal `"__EFMigrationsHistory_profiles"` with `ProfilesDbContext.MigrationsHistoryTable`.

`ProfilesDbContextFactory.cs` (same shape as `TenancyDbContextFactory`):

```csharp
using Crm.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crm.Profiles.Infrastructure;

/// <summary>Design-time entry point for <c>dotnet ef</c>; connection string from <c>ConnectionStrings__Crm</c>, else the local compose default.</summary>
public sealed class ProfilesDbContextFactory : IDesignTimeDbContextFactory<ProfilesDbContext>
{
    private const string LocalDefault = "Server=localhost,1433;Database=crm;User Id=sa;Password=Crm_Dev_Passw0rd!;TrustServerCertificate=True;Encrypt=False";

    public ProfilesDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Crm");
        var options = new DbContextOptionsBuilder<ProfilesDbContext>()
            .UseSqlServer(
                string.IsNullOrWhiteSpace(connectionString) ? LocalDefault : connectionString,
                sql => sql.MigrationsHistoryTable(ProfilesDbContext.MigrationsHistoryTable, ProfilesDbContext.Schema))
            .Options;
        return new ProfilesDbContext(options, new TenantContext());
    }
}
```

- [ ] **Step 4: Scaffold the migration, then edit it for partitioning**

```bash
dotnet tool restore
dotnet ef migrations add InitialProfiles \
  --project src/Modules/Profiles/Profiles.Infrastructure \
  --startup-project src/Modules/Profiles/Profiles.Infrastructure \
  --context ProfilesDbContext --output-dir Migrations
```

(No database is contacted.) Edit **only** `Migrations/<timestamp>_InitialProfiles.cs` — never the `.Designer.cs` or the snapshot:

1. Directly after `migrationBuilder.EnsureSchema(name: "profiles");` insert:

   ```csharp
            // EF cannot express partitioning. RANGE RIGHT: each boundary is the first instant of a month.
            // EventPartitionMaintenanceService adds later months; these two years make a fresh database usable at once.
            migrationBuilder.Sql(
                """
                CREATE PARTITION FUNCTION PF_Events_Monthly (datetime2(3)) AS RANGE RIGHT FOR VALUES (
                    '2026-01-01', '2026-02-01', '2026-03-01', '2026-04-01', '2026-05-01', '2026-06-01',
                    '2026-07-01', '2026-08-01', '2026-09-01', '2026-10-01', '2026-11-01', '2026-12-01',
                    '2027-01-01', '2027-02-01', '2027-03-01', '2027-04-01', '2027-05-01', '2027-06-01',
                    '2027-07-01', '2027-08-01', '2027-09-01', '2027-10-01', '2027-11-01', '2027-12-01');
                CREATE PARTITION SCHEME PS_Events_Monthly AS PARTITION PF_Events_Monthly ALL TO ([PRIMARY]);
                """);
   ```

2. In the `CreateTable(name: "Events", …)` call delete the `table.PrimaryKey("UX_Events_Id", …)` line (keep the check constraint). A table without a primary-key constraint is intended here.
3. Delete the two scaffolded `migrationBuilder.CreateIndex(…)` calls for `CX_Events_TenantId_ProfileId_OccurredAt` and `IX_Events_TenantId_Name_OccurredAt`.
4. After the last `CreateTable`/`CreateIndex` call of `Up` append:

   ```csharp
            // Creating the clustered index on the scheme moves the table onto it; the other two are aligned explicitly.
            migrationBuilder.Sql(
                """
                CREATE CLUSTERED INDEX CX_Events_TenantId_ProfileId_OccurredAt
                    ON profiles.Events (TenantId, ProfileId, OccurredAt) ON PS_Events_Monthly (OccurredAt);
                CREATE UNIQUE NONCLUSTERED INDEX UX_Events_Id
                    ON profiles.Events (Id, OccurredAt) ON PS_Events_Monthly (OccurredAt);
                CREATE NONCLUSTERED INDEX IX_Events_TenantId_Name_OccurredAt
                    ON profiles.Events (TenantId, Name, OccurredAt) INCLUDE (ProfileId) ON PS_Events_Monthly (OccurredAt);
                ALTER TABLE profiles.Events ADD CONSTRAINT FK_Events_ProfileId
                    FOREIGN KEY (ProfileId) REFERENCES profiles.Profiles (Id);
                ALTER TABLE profiles.Profiles ADD CONSTRAINT FK_Profiles_MergedIntoId
                    FOREIGN KEY (MergedIntoId) REFERENCES profiles.Profiles (Id);
                """);
   ```

5. At the **end** of `Down` (after the scaffolded `DropTable` calls) append:

   ```csharp
            migrationBuilder.Sql(
                """
                DROP PARTITION SCHEME PS_Events_Monthly;
                DROP PARTITION FUNCTION PF_Events_Monthly;
                """);
   ```

If the scaffold emitted anything this plan does not mention (an unexpected index, a differently named constraint), fix the **configuration** and re-scaffold (`dotnet ef migrations remove` first) rather than hand-editing more of the migration; report what it was.

- [ ] **Step 5: `make migrate` and the test fixture**

`Makefile` — change the `migrate` help text to `(module contexts with migrations: Tenancy, Profiles)` and append to the `migrate-run` recipe:

```make
	ConnectionStrings__Crm="$(CRM_CONNECTION_STRING)" dotnet ef database update \
		--project src/Modules/Profiles/Profiles.Infrastructure \
		--startup-project src/Modules/Profiles/Profiles.Infrastructure \
		--context ProfilesDbContext
```

(Recipe lines start with a TAB. Do not run `make migrate`; the integration fixture exercises the migration.)

`tests/Crm.IntegrationTests/Fixtures/CrmStackFixture.cs` — add `using Crm.Profiles.Infrastructure;`, call `await MigrateProfilesAsync();` right after `await MigrateTenancyAsync();`, and add:

```csharp
    private async Task MigrateProfilesAsync()
    {
        var options = new DbContextOptionsBuilder<ProfilesDbContext>()
            .UseSqlServer(SqlConnectionString, sql => sql.MigrationsHistoryTable(ProfilesDbContext.MigrationsHistoryTable, ProfilesDbContext.Schema))
            .Options;
        await using var db = new ProfilesDbContext(options, new TenantContext());
        await db.Database.MigrateAsync();
    }
```

- [ ] **Step 6: Verify**

Run: `dotnet build Crm.sln -warnaserror` → 0 warnings. `dotnet test Crm.sln --filter "Category!=Integration"` → green (AT-006 now sees `Profile`, `UserIdentity`, `EventRecord`). Then the SchemaTests command from Step 2 → 5 passed. Then `dotnet ef migrations has-pending-model-changes --project src/Modules/Profiles/Profiles.Infrastructure --startup-project src/Modules/Profiles/Profiles.Infrastructure --context ProfilesDbContext` → "No changes have been made to the model since the last migration."

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Profiles/Profiles.Infrastructure Makefile tests/Crm.IntegrationTests/Fixtures/CrmStackFixture.cs tests/Crm.IntegrationTests/Profiles/SchemaTests.cs
git commit -m "feat(profiles): schema profiles with month-partitioned Events, promoted attribute columns and make migrate

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Application — decoder, per-message pipeline, processed messages, retry policy

**Files:**
- Create: `src/Modules/Profiles/Profiles.Application/Messages/RawEnvelope.cs`, `RawMessage.cs`, `PoisonMessageException.cs`, `RawMessageDecoder.cs`, `ProcessedMessage.cs`, `ProcessedMessageFactory.cs`, `ProcessingOutcome.cs`
- Create: `src/Modules/Profiles/Profiles.Application/Pipeline/IProfileStore.cs`, `IPipelineHooks.cs`, `IRawMessageHandler.cs`, `ProfilePipeline.cs`
- Create: `src/Modules/Profiles/Profiles.Application/Retry/MessageRetryPolicy.cs`
- Test: `tests/Profiles.UnitTests/InMemoryProfileStore.cs`, `RawMessageDecoderTests.cs`, `ProfilePipelineTrackTests.cs`, `ProfilePipelineIdentifyTests.cs`, `MessageRetryPolicyTests.cs`

**Interfaces:**
- Consumes: Task 1 domain types; `Crm.Contracts` (`TrackEventRaw`, `IdentifyRaw`, `EventTracked`, `ProfileUpdated`, `ProfileMerged`, `ProfileSnapshot`, `KafkaHeaders`, `ContractJson.Options`).
- Produces:
  - `Crm.Profiles.Application.Messages`: `sealed record RawEnvelope(string Topic, int Partition, long Offset, string? Key, byte[] Value, IReadOnlyDictionary<string, string> Headers)`; `abstract record RawMessage(Guid TenantId)`, `sealed record TrackMessage(TrackEventRaw Payload)`, `sealed record IdentifyMessage(IdentifyRaw Payload)`; `static RawMessage RawMessageDecoder.Decode(RawEnvelope)` (throws `PoisonMessageException`); `sealed record ProcessedMessage(string Key, string MessageType, Guid TenantId, byte[] Value)`; `sealed record ProcessingOutcome(IReadOnlyList<ProcessedMessage> Messages, Guid ProfileId, bool Duplicate, bool Redirected, bool Merged)`
  - `Crm.Profiles.Application.Pipeline`: `IProfileStore` (below), `IPipelineHooks { Task MergeLocksTakenAsync(Guid tenantId, CancellationToken); Task SqlCommittedAsync(RawEnvelope envelope, CancellationToken); }`, `sealed class NoPipelineHooks : IPipelineHooks`, `IRawMessageHandler { Task<ProcessingOutcome> HandleAsync(RawEnvelope, CancellationToken); }`, `sealed class ProfilePipeline(IProfileStore store, IPipelineHooks hooks) { Task<ProcessingOutcome> HandleAsync(RawMessage, CancellationToken); }`
  - `Crm.Profiles.Application.Retry`: `sealed record RetryResult<T>(T? Value, Exception? Failure, int Attempts)`, `sealed class MessageRetryPolicy(IReadOnlyList<TimeSpan> delays, Func<TimeSpan, CancellationToken, Task>? delay = null) { int MaxAttempts; Task<RetryResult<T>> ExecuteAsync<T>(Func<int, CancellationToken, Task<T>> action, CancellationToken ct); }`

- [ ] **Step 1: The store port and the small types (no tests of their own)**

`Pipeline/IProfileStore.cs`:

```csharp
using Crm.Profiles.Domain;

namespace Crm.Profiles.Application.Pipeline;

/// <summary>
/// Everything the pipeline needs from SQL and the identity cache for ONE message. Scoped: one instance, one
/// transaction, one tenant. Nothing is visible to other transactions before <see cref="CommitAsync"/>.
/// </summary>
public interface IProfileStore
{
    Task BeginAsync(CancellationToken cancellationToken);

    /// <summary>Identity cache first (unless bypassed), then profiles.UserIdentities. A database hit is remembered after the commit.</summary>
    Task<Guid?> FindProfileIdAsync(IdentityKey key, bool bypassCache, CancellationToken cancellationToken);

    /// <summary>Loads the row WITH (UPDLOCK, HOLDLOCK) and tracks it; null when the tenant has no such profile.</summary>
    Task<Profile?> LockProfileAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Tracked, so re-pointing them is saved by <see cref="CommitAsync"/>.</summary>
    Task<IReadOnlyList<UserIdentity>> GetIdentitiesAsync(Guid profileId, CancellationToken cancellationToken);

    void Add(Profile profile);

    void Add(UserIdentity identity);

    /// <summary>Inserts unless (Id, OccurredAt) already exists. False = duplicate (FR-06). Pending profile changes are flushed first so the foreign key holds.</summary>
    Task<bool> TryAppendEventAsync(EventRecord record, CancellationToken cancellationToken);

    Task RepointEventsAsync(Guid loserId, Guid winnerId, CancellationToken cancellationToken);

    Task<long> CountEventsAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Cache write, applied after the commit.</summary>
    void Remember(IdentityKey key, Guid profileId);

    /// <summary>Cache delete, applied inside the transaction before the commit; cancels a pending write for the key.</summary>
    void Forget(IdentityKey key);

    Task CommitAsync(CancellationToken cancellationToken);
}
```

`Pipeline/IPipelineHooks.cs`:

```csharp
using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Application.Pipeline;

/// <summary>
/// Seams for the two orderings the integration tests must prove (IT-006, IT-008). Production uses
/// <see cref="NoPipelineHooks"/>. Both calls carry enough context for a test to react to its own tenant only.
/// </summary>
public interface IPipelineHooks
{
    /// <summary>Both profile rows of a merge are locked; nothing has been changed yet.</summary>
    Task MergeLocksTakenAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The SQL transaction for this record committed; nothing was published and the Kafka offset is not committed yet.</summary>
    Task SqlCommittedAsync(RawEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class NoPipelineHooks : IPipelineHooks
{
    public Task MergeLocksTakenAsync(Guid tenantId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SqlCommittedAsync(RawEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

`Pipeline/IRawMessageHandler.cs`:

```csharp
using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Application.Pipeline;

/// <summary>Decode → tenant scope → <see cref="ProfilePipeline"/> → commit, for one consumed record. One call is one attempt.</summary>
public interface IRawMessageHandler
{
    Task<ProcessingOutcome> HandleAsync(RawEnvelope envelope, CancellationToken cancellationToken);
}
```

`Messages/RawEnvelope.cs`:

```csharp
namespace Crm.Profiles.Application.Messages;

/// <summary>A consumed Kafka record without Confluent types, so the application layer stays transport-free.</summary>
public sealed record RawEnvelope(string Topic, int Partition, long Offset, string? Key, byte[] Value, IReadOnlyDictionary<string, string> Headers);
```

`Messages/RawMessage.cs`:

```csharp
using Crm.Contracts;

namespace Crm.Profiles.Application.Messages;

public abstract record RawMessage(Guid TenantId);

public sealed record TrackMessage(TrackEventRaw Payload) : RawMessage(Payload.TenantId);

public sealed record IdentifyMessage(IdentifyRaw Payload) : RawMessage(Payload.TenantId);
```

`Messages/PoisonMessageException.cs`:

```csharp
namespace Crm.Profiles.Application.Messages;

/// <summary>The record can never be processed: wrong shape, wrong tenant, unknown type.</summary>
public sealed class PoisonMessageException : Exception
{
    public PoisonMessageException()
    {
    }

    public PoisonMessageException(string message)
        : base(message)
    {
    }

    public PoisonMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

`Messages/ProcessedMessage.cs` and `Messages/ProcessingOutcome.cs`:

```csharp
namespace Crm.Profiles.Application.Messages;

/// <summary>One record for events.processed: key {tenantId:N}:{profileId:N}, JSON value, type for the message-type header.</summary>
public sealed record ProcessedMessage(string Key, string MessageType, Guid TenantId, byte[] Value);
```

```csharp
namespace Crm.Profiles.Application.Messages;

/// <param name="Messages">To publish after the SQL commit, in order.</param>
/// <param name="ProfileId">The live profile the message was applied to.</param>
/// <param name="Duplicate">The event already existed; nothing was written (FR-06). EventTracked is still in <paramref name="Messages"/> (ruling E7).</param>
/// <param name="Redirected">Resolution started at a merge loser and followed MergedIntoId.</param>
public sealed record ProcessingOutcome(IReadOnlyList<ProcessedMessage> Messages, Guid ProfileId, bool Duplicate, bool Redirected, bool Merged);
```

- [ ] **Step 2: Write the failing decoder and retry tests**

`tests/Profiles.UnitTests/RawMessageDecoderTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class RawMessageDecoderTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime Now = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    private static RawEnvelope Envelope(object payload, string messageType, Guid? headerTenant = null)
        => new(KafkaTopics.EventsRaw, 3, 42, "key", JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options), new Dictionary<string, string>
        {
            [KafkaHeaders.TenantId] = (headerTenant ?? Tenant).ToString("N"),
            [KafkaHeaders.MessageType] = messageType,
        });

    private static TrackEventRaw Track(string? userId = "u1", string? anonymousId = null, string name = "purchase")
        => new(Guid.CreateVersion7(), Tenant, userId, anonymousId, name, JsonDocument.Parse("""{"amount":49.9}""").RootElement.Clone(), Now, Now);

    [Fact]
    public void A_track_record_decodes_to_a_TrackMessage()
    {
        var decoded = RawMessageDecoder.Decode(Envelope(Track(), nameof(TrackEventRaw)));

        decoded.Should().BeOfType<TrackMessage>().Which.Payload.Event.Should().Be("purchase");
        decoded.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public void An_identify_record_decodes_to_an_IdentifyMessage()
    {
        var payload = new IdentifyRaw(Guid.CreateVersion7(), Tenant, "u1", "a1", JsonDocument.Parse("""{"plan":"pro"}""").RootElement.Clone(), Now);

        RawMessageDecoder.Decode(Envelope(payload, nameof(IdentifyRaw))).Should().BeOfType<IdentifyMessage>().Which.Payload.AnonymousId.Should().Be("a1");
    }

    [Fact]
    public void Header_and_payload_tenant_must_agree()
    {
        var act = () => RawMessageDecoder.Decode(Envelope(Track(), nameof(TrackEventRaw), headerTenant: Guid.CreateVersion7()));

        act.Should().Throw<PoisonMessageException>().WithMessage("*tenant*");
    }

    [Fact]
    public void Malformed_json_is_poison()
    {
        var envelope = Envelope(Track(), nameof(TrackEventRaw)) with { Value = Encoding.UTF8.GetBytes("""{"eventId":"x","properties":{"a":""") };

        var act = () => RawMessageDecoder.Decode(envelope);

        act.Should().Throw<PoisonMessageException>().WithInnerException<JsonException>();
    }

    [Fact]
    public void Missing_headers_and_unknown_types_are_poison()
    {
        var noHeaders = Envelope(Track(), nameof(TrackEventRaw)) with { Headers = new Dictionary<string, string>() };
        var unknownType = Envelope(Track(), "SomethingElse");

        FluentActions.Invoking(() => RawMessageDecoder.Decode(noHeaders)).Should().Throw<PoisonMessageException>();
        FluentActions.Invoking(() => RawMessageDecoder.Decode(unknownType)).Should().Throw<PoisonMessageException>().WithMessage("*SomethingElse*");
    }

    [Fact]
    public void A_track_without_any_identifier_or_without_a_name_is_poison()
    {
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(userId: null, anonymousId: " "), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(name: ""), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
    }

    [Fact]
    public void Values_that_cannot_be_stored_are_poison()
    {
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(userId: new string('u', 129)), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(userId: null, anonymousId: new string('a', 257)), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(Track(name: new string('e', 65)), nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
    }

    [Fact]
    public void Properties_must_be_an_object_when_present()
    {
        var payload = Track() with { Properties = JsonDocument.Parse("[1]").RootElement.Clone() };

        FluentActions.Invoking(() => RawMessageDecoder.Decode(Envelope(payload, nameof(TrackEventRaw)))).Should().Throw<PoisonMessageException>();
    }
}
```

`tests/Profiles.UnitTests/MessageRetryPolicyTests.cs`:

```csharp
using Crm.Profiles.Application.Retry;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class MessageRetryPolicyTests
{
    private readonly List<TimeSpan> _waits = [];

    private MessageRetryPolicy Policy() => new(
        [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2)],
        (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Success_on_the_first_attempt_does_not_wait()
    {
        var result = await Policy().ExecuteAsync((attempt, _) => Task.FromResult(attempt), CancellationToken.None);

        result.Should().BeEquivalentTo(new { Value = 1, Attempts = 1, Failure = (Exception?)null });
        _waits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_is_retried_with_the_configured_delays()
    {
        var result = await Policy().ExecuteAsync(
            (attempt, _) => attempt < 3 ? throw new InvalidOperationException("transient") : Task.FromResult("ok"),
            CancellationToken.None);

        result.Value.Should().Be("ok");
        result.Attempts.Should().Be(3);
        _waits.Should().Equal(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task After_three_attempts_the_last_exception_is_reported_not_thrown()
    {
        var result = await Policy().ExecuteAsync<string>((attempt, _) => throw new InvalidOperationException($"boom {attempt}"), CancellationToken.None);

        result.Failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom 3");
        result.Attempts.Should().Be(3);
        Policy().MaxAttempts.Should().Be(3);
    }

    [Fact]
    public async Task Shutdown_is_not_a_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Policy().ExecuteAsync<string>((_, ct) => throw new OperationCanceledException(ct), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

Run: `dotnet test tests/Profiles.UnitTests` → build FAILS (`RawMessageDecoder`, `MessageRetryPolicy` missing).

- [ ] **Step 3: Implement decoder and retry policy**

`Messages/RawMessageDecoder.cs`:

```csharp
using System.Text.Json;
using Crm.Contracts;

namespace Crm.Profiles.Application.Messages;

/// <summary>
/// Bytes → contract, plus the checks that keep a bad record from reaching SQL: the header tenant must equal the
/// payload tenant (spec §2.5), and nothing may exceed what the schema can store (the API enforces the same limits,
/// but events.raw is not writable by the API alone).
/// </summary>
public static class RawMessageDecoder
{
    private const int MaxUserId = 128;
    private const int MaxAnonymousId = 256;
    private const int MaxEventName = 64;

    public static RawMessage Decode(RawEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!envelope.Headers.TryGetValue(KafkaHeaders.TenantId, out var tenantText) || !Guid.TryParse(tenantText, out var headerTenant))
        {
            throw new PoisonMessageException($"Header '{KafkaHeaders.TenantId}' is missing or not a GUID.");
        }

        if (!envelope.Headers.TryGetValue(KafkaHeaders.MessageType, out var messageType))
        {
            throw new PoisonMessageException($"Header '{KafkaHeaders.MessageType}' is missing.");
        }

        RawMessage message = messageType switch
        {
            nameof(TrackEventRaw) => new TrackMessage(Validate(Read<TrackEventRaw>(envelope))),
            nameof(IdentifyRaw) => new IdentifyMessage(Validate(Read<IdentifyRaw>(envelope))),
            _ => throw new PoisonMessageException($"Unknown message type '{messageType}'."),
        };

        return message.TenantId == headerTenant
            ? message
            : throw new PoisonMessageException($"Header tenant {headerTenant:N} does not match payload tenant {message.TenantId:N}.");
    }

    private static T Read<T>(RawEnvelope envelope)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(envelope.Value, ContractJson.Options)
                ?? throw new PoisonMessageException($"{typeof(T).Name} payload is JSON null.");
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException($"{typeof(T).Name} payload is not valid JSON: {ex.Message}", ex);
        }
    }

    private static TrackEventRaw Validate(TrackEventRaw raw)
    {
        RequireEnvelopeFields(raw.EventId, raw.TenantId, raw.ReceivedAt);
        Require(!string.IsNullOrWhiteSpace(raw.Event) && raw.Event.Length <= MaxEventName, "event name is missing or longer than 64 characters");
        Require(!string.IsNullOrWhiteSpace(raw.UserId) || !string.IsNullOrWhiteSpace(raw.AnonymousId), "neither userId nor anonymousId is present");
        RequireLengths(raw.UserId, raw.AnonymousId);
        RequireObjectOrAbsent(raw.Properties, "properties");
        return raw;
    }

    private static IdentifyRaw Validate(IdentifyRaw raw)
    {
        RequireEnvelopeFields(raw.EventId, raw.TenantId, raw.ReceivedAt);
        Require(!string.IsNullOrWhiteSpace(raw.UserId), "userId is missing");
        RequireLengths(raw.UserId, raw.AnonymousId);
        RequireObjectOrAbsent(raw.Traits, "traits");
        return raw;
    }

    private static void RequireEnvelopeFields(Guid eventId, Guid tenantId, DateTime receivedAt)
        => Require(eventId != Guid.Empty && tenantId != Guid.Empty && receivedAt != default, "eventId, tenantId or receivedAt is missing");

    private static void RequireLengths(string? userId, string? anonymousId)
        => Require((userId?.Length ?? 0) <= MaxUserId && (anonymousId?.Length ?? 0) <= MaxAnonymousId, "userId or anonymousId is longer than the schema allows");

    private static void RequireObjectOrAbsent(JsonElement element, string name)
        => Require(element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object, $"{name} is not a JSON object");

    private static void Require(bool condition, string problem)
    {
        if (!condition)
        {
            throw new PoisonMessageException($"Message rejected: {problem}.");
        }
    }
}
```

`Retry/MessageRetryPolicy.cs`:

```csharp
namespace Crm.Profiles.Application.Retry;

public sealed record RetryResult<T>(T? Value, Exception? Failure, int Attempts)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Spec §4.3: a message gets delays.Count + 1 attempts (3 with the default 200 ms and 2 s); after the last one the
/// failure is handed back so the caller can dead-letter it. Shutdown is never counted as a failure.
/// </summary>
public sealed class MessageRetryPolicy(IReadOnlyList<TimeSpan> delays, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public int MaxAttempts => delays.Count + 1;

    public async Task<RetryResult<T>> ExecuteAsync<T>(Func<int, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new RetryResult<T>(await action(attempt, cancellationToken).ConfigureAwait(false), null, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= MaxAttempts)
                {
                    return new RetryResult<T>(default, ex, attempt);
                }

                await _delay(delays[attempt - 1], cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
```

Run: `dotnet test tests/Profiles.UnitTests --filter "FullyQualifiedName~RawMessageDecoderTests|FullyQualifiedName~MessageRetryPolicyTests"` → PASS.

- [ ] **Step 4: Write the failing pipeline tests**

`tests/Profiles.UnitTests/InMemoryProfileStore.cs`:

```csharp
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Domain;

namespace Crm.Profiles.UnitTests;

/// <summary>The store port without SQL. `Cache` simulates identity-cache entries, including stale ones.</summary>
internal sealed class InMemoryProfileStore : IProfileStore
{
    public Dictionary<Guid, Profile> Profiles { get; } = [];

    public List<UserIdentity> Identities { get; } = [];

    public List<EventRecord> Events { get; } = [];

    /// <summary>EventRecord.ProfileId is immutable in the domain (SQL re-points rows), so ownership is tracked here.</summary>
    public Dictionary<Guid, Guid> EventOwners { get; } = [];

    public Dictionary<IdentityKey, Guid> Cache { get; } = [];

    public Dictionary<IdentityKey, Guid> Remembered { get; } = [];

    public List<IdentityKey> Forgotten { get; } = [];

    public List<string> Calls { get; } = [];

    public Task BeginAsync(CancellationToken cancellationToken)
    {
        Calls.Add("begin");
        return Task.CompletedTask;
    }

    public Task<Guid?> FindProfileIdAsync(IdentityKey key, bool bypassCache, CancellationToken cancellationToken)
    {
        if (!bypassCache && Cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult<Guid?>(cached);
        }

        return Task.FromResult(Identities.FirstOrDefault(i => i.Key == key)?.ProfileId);
    }

    public Task<Profile?> LockProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        Calls.Add($"lock:{profileId:N}");
        return Task.FromResult(Profiles.GetValueOrDefault(profileId));
    }

    public Task<IReadOnlyList<UserIdentity>> GetIdentitiesAsync(Guid profileId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<UserIdentity>>(Identities.Where(i => i.ProfileId == profileId).ToList());

    public void Add(Profile profile) => Profiles[profile.Id] = profile;

    public void Add(UserIdentity identity) => Identities.Add(identity);

    public Task<bool> TryAppendEventAsync(EventRecord record, CancellationToken cancellationToken)
    {
        if (Events.Any(e => e.Id == record.Id && e.OccurredAt == record.OccurredAt))
        {
            return Task.FromResult(false);
        }

        Events.Add(record);
        EventOwners[record.Id] = record.ProfileId;
        return Task.FromResult(true);
    }

    public Task RepointEventsAsync(Guid loserId, Guid winnerId, CancellationToken cancellationToken)
    {
        foreach (var eventId in EventOwners.Where(o => o.Value == loserId).Select(o => o.Key).ToList())
        {
            EventOwners[eventId] = winnerId;
        }

        return Task.CompletedTask;
    }

    public Task<long> CountEventsAsync(Guid profileId, CancellationToken cancellationToken)
        => Task.FromResult((long)EventOwners.Count(o => o.Value == profileId));

    public void Remember(IdentityKey key, Guid profileId) => Remembered[key] = profileId;

    public void Forget(IdentityKey key)
    {
        Remembered.Remove(key);
        Forgotten.Add(key);
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        Calls.Add("commit");
        return Task.CompletedTask;
    }

    public Profile ProfileOf(IdentityKey key) => Profiles[Identities.Single(i => i.Key == key).ProfileId];
}
```

`tests/Profiles.UnitTests/ProfilePipelineTrackTests.cs`:

```csharp
using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ProfilePipelineTrackTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
    private readonly InMemoryProfileStore _store = new();

    private ProfilePipeline Pipeline() => new(_store, new NoPipelineHooks());

    internal static TrackMessage Track(Guid tenant, string? userId, string? anonymousId, DateTime at, Guid? eventId = null, string name = "purchase")
        => new(new TrackEventRaw(eventId ?? Guid.CreateVersion7(), tenant, userId, anonymousId, name, JsonDocument.Parse("""{"amount":49.9}""").RootElement.Clone(), at, at));

    [Fact]
    public async Task First_track_creates_profile_identity_and_event_inside_one_transaction()
    {
        var outcome = await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0), CancellationToken.None);

        var profile = _store.ProfileOf(IdentityKey.ExternalId("u1"));
        profile.ExternalId.Should().Be("u1");
        profile.EventCount.Should().Be(1);
        profile.FirstSeenAt.Should().Be(T0);
        _store.Events.Should().ContainSingle().Which.ProfileId.Should().Be(profile.Id);
        _store.Calls.First().Should().Be("begin");
        _store.Calls.Last().Should().Be("commit");
        _store.Remembered[IdentityKey.ExternalId("u1")].Should().Be(profile.Id);
        outcome.Should().BeEquivalentTo(new { ProfileId = profile.Id, Duplicate = false, Redirected = false, Merged = false });
    }

    [Fact]
    public async Task EventTracked_is_keyed_by_tenant_and_profile_and_carries_the_snapshot()
    {
        var message = Track(Tenant, "u1", null, T0);

        var outcome = await Pipeline().HandleAsync(message, CancellationToken.None);

        var processed = outcome.Messages.Should().ContainSingle().Subject;
        processed.MessageType.Should().Be(nameof(EventTracked));
        processed.TenantId.Should().Be(Tenant);
        processed.Key.Should().Be($"{Tenant:N}:{outcome.ProfileId:N}");
        var payload = JsonSerializer.Deserialize<EventTracked>(processed.Value, ContractJson.Options)!;
        payload.EventId.Should().Be(message.Payload.EventId);
        payload.ProfileId.Should().Be(outcome.ProfileId);
        payload.Event.Should().Be("purchase");
        payload.Properties.GetProperty("amount").GetDouble().Should().Be(49.9);
        payload.Profile.ExternalId.Should().Be("u1");
        payload.Profile.Attributes.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task FR06_a_duplicate_event_writes_nothing_but_is_announced_again()
    {
        var message = Track(Tenant, "u1", null, T0);
        var first = await Pipeline().HandleAsync(message, CancellationToken.None);

        var second = await Pipeline().HandleAsync(message, CancellationToken.None);

        second.Duplicate.Should().BeTrue();
        _store.Events.Should().ContainSingle();
        _store.ProfileOf(IdentityKey.ExternalId("u1")).EventCount.Should().Be(1);
        // Ruling E7: the first delivery may have died between the SQL commit and the produce.
        second.Messages.Should().ContainSingle().Which.MessageType.Should().Be(nameof(EventTracked));
        second.Messages[0].Key.Should().Be(first.Messages[0].Key);
    }

    [Fact]
    public async Task Absent_properties_are_stored_as_an_empty_object()
    {
        var raw = Track(Tenant, "u1", null, T0).Payload with { Properties = default };

        await Pipeline().HandleAsync(new TrackMessage(raw), CancellationToken.None);

        _store.Events.Single().Properties.Should().Be("{}");
    }

    [Fact]
    public async Task E5_track_with_both_ids_attaches_the_unmapped_anonymous_id_to_the_known_profile()
    {
        await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0), CancellationToken.None);

        await Pipeline().HandleAsync(Track(Tenant, "u1", "a1", T0.AddMinutes(1)), CancellationToken.None);

        _store.Profiles.Should().ContainSingle();
        _store.ProfileOf(IdentityKey.AnonymousId("a1")).ExternalId.Should().Be("u1");
        _store.ProfileOf(IdentityKey.ExternalId("u1")).EventCount.Should().Be(2);
    }

    [Fact]
    public async Task E5_track_with_both_ids_promotes_an_anonymous_profile_that_has_no_external_id()
    {
        await Pipeline().HandleAsync(Track(Tenant, null, "a1", T0), CancellationToken.None);

        await Pipeline().HandleAsync(Track(Tenant, "u1", "a1", T0.AddMinutes(1)), CancellationToken.None);

        _store.Profiles.Should().ContainSingle();
        _store.ProfileOf(IdentityKey.ExternalId("u1")).EventCount.Should().Be(2);
    }

    [Fact]
    public async Task E5_track_never_merges_two_existing_profiles()
    {
        await Pipeline().HandleAsync(Track(Tenant, null, "a1", T0), CancellationToken.None);
        await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0.AddMinutes(1)), CancellationToken.None);

        var outcome = await Pipeline().HandleAsync(Track(Tenant, "u1", "a1", T0.AddMinutes(2)), CancellationToken.None);

        outcome.Merged.Should().BeFalse();
        _store.Profiles.Should().HaveCount(2);
        outcome.ProfileId.Should().Be(_store.ProfileOf(IdentityKey.ExternalId("u1")).Id);
        _store.ProfileOf(IdentityKey.AnonymousId("a1")).EventCount.Should().Be(1);
    }

    [Fact]
    public async Task UT013_an_event_that_resolves_to_a_merge_loser_is_applied_to_the_winner()
    {
        await Pipeline().HandleAsync(Track(Tenant, null, "a1", T0), CancellationToken.None);
        await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0.AddMinutes(1)), CancellationToken.None);
        var winner = _store.ProfileOf(IdentityKey.AnonymousId("a1"));
        var loser = _store.ProfileOf(IdentityKey.ExternalId("u1"));
        ProfileMerge.Apply(winner, loser, _store.Identities.Where(i => i.ProfileId == loser.Id).ToList());
        _store.Cache[IdentityKey.ExternalId("u1")] = loser.Id; // what a reader cached just before the merge committed

        var outcome = await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0.AddMinutes(2)), CancellationToken.None);

        outcome.Redirected.Should().BeTrue();
        outcome.ProfileId.Should().Be(winner.Id);
        winner.EventCount.Should().Be(2);
        _store.Forgotten.Should().Contain(IdentityKey.ExternalId("u1"));
        _store.Remembered[IdentityKey.ExternalId("u1")].Should().Be(winner.Id);
    }

    [Fact]
    public async Task A_cached_id_whose_profile_is_gone_falls_back_to_the_database()
    {
        await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0), CancellationToken.None);
        _store.Cache[IdentityKey.ExternalId("u1")] = Guid.CreateVersion7();

        var outcome = await Pipeline().HandleAsync(Track(Tenant, "u1", null, T0.AddMinutes(1)), CancellationToken.None);

        outcome.ProfileId.Should().Be(_store.ProfileOf(IdentityKey.ExternalId("u1")).Id);
        _store.Profiles.Should().ContainSingle();
        _store.Forgotten.Should().Contain(IdentityKey.ExternalId("u1"));
    }
}
```

`tests/Profiles.UnitTests/ProfilePipelineIdentifyTests.cs`:

```csharp
using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Domain;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ProfilePipelineIdentifyTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
    private readonly InMemoryProfileStore _store = new();
    private readonly IPipelineHooks _hooks = Substitute.For<IPipelineHooks>();

    private ProfilePipeline Pipeline() => new(_store, _hooks);

    private static IdentifyMessage Identify(string userId, string? anonymousId, string traits, DateTime at)
        => new(new IdentifyRaw(Guid.CreateVersion7(), Tenant, userId, anonymousId, JsonDocument.Parse(traits).RootElement.Clone(), at));

    private Task<ProcessingOutcome> TrackAsync(string? userId, string? anonymousId, DateTime at)
        => Pipeline().HandleAsync(ProfilePipelineTrackTests.Track(Tenant, userId, anonymousId, at), CancellationToken.None);

    [Fact]
    public async Task IT002_shape_identify_creates_a_profile_applies_traits_and_never_counts_as_an_event()
    {
        await Pipeline().HandleAsync(Identify("u2", null, """{"plan":"pro"}""", T0), CancellationToken.None);
        var outcome = await Pipeline().HandleAsync(Identify("u2", null, """{"plan":"free","city":"SG"}""", T0.AddMinutes(1)), CancellationToken.None);

        var profile = _store.ProfileOf(IdentityKey.ExternalId("u2"));
        _store.Profiles.Should().ContainSingle();
        profile.Attributes.Should().Be("""{"plan":"free","city":"SG"}""");
        profile.EventCount.Should().Be(0);
        profile.LastSeenAt.Should().Be(T0.AddMinutes(1));
        _store.Events.Should().BeEmpty();
        var updated = JsonSerializer.Deserialize<ProfileUpdated>(outcome.Messages.Should().ContainSingle().Subject.Value, ContractJson.Options)!;
        updated.ChangedKeys.Should().Equal("plan", "city");
        updated.ProfileId.Should().Be(profile.Id);
        updated.OccurredAt.Should().Be(T0.AddMinutes(1));
    }

    [Fact]
    public async Task An_identify_that_changes_nothing_on_an_existing_profile_publishes_nothing()
    {
        await Pipeline().HandleAsync(Identify("u2", null, """{"plan":"pro"}""", T0), CancellationToken.None);

        var outcome = await Pipeline().HandleAsync(Identify("u2", null, """{"plan":"pro"}""", T0.AddMinutes(1)), CancellationToken.None);

        outcome.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task IT007_shape_identify_merges_the_anonymous_profile_into_the_known_one_older_wins()
    {
        await TrackAsync(null, "a1", T0);
        await TrackAsync(null, "a1", T0.AddMinutes(1));
        await TrackAsync("u1", null, T0.AddMinutes(2));
        var p1 = _store.ProfileOf(IdentityKey.AnonymousId("a1"));
        var p2 = _store.ProfileOf(IdentityKey.ExternalId("u1"));

        var outcome = await Pipeline().HandleAsync(Identify("u1", "a1", """{"plan":"pro"}""", T0.AddMinutes(3)), CancellationToken.None);

        outcome.Merged.Should().BeTrue();
        outcome.ProfileId.Should().Be(p1.Id);
        p2.MergedIntoId.Should().Be(p1.Id);
        p1.ExternalId.Should().Be("u1");
        p1.EventCount.Should().Be(3, "recomputed as COUNT(*) of the winner's events (IT-010)");
        _store.EventOwners.Values.Should().OnlyContain(owner => owner == p1.Id);
        _store.Identities.Should().OnlyContain(i => i.ProfileId == p1.Id);
        _store.Forgotten.Should().Contain(IdentityKey.ExternalId("u1"), "the loser's identities leave the cache before the commit (IT-009)");
        await _hooks.Received(1).MergeLocksTakenAsync(Tenant, Arg.Any<CancellationToken>());

        outcome.Messages.Select(m => m.MessageType).Should().Equal(nameof(ProfileMerged), nameof(ProfileUpdated));
        var merged = JsonSerializer.Deserialize<ProfileMerged>(outcome.Messages[0].Value, ContractJson.Options)!;
        merged.WinnerProfileId.Should().Be(p1.Id);
        merged.LoserProfileId.Should().Be(p2.Id);
        outcome.Messages[0].Key.Should().Be($"{Tenant:N}:{p1.Id:N}");
    }

    [Fact]
    public async Task E5_an_anonymous_profile_that_already_belongs_to_another_user_is_not_merged()
    {
        await TrackAsync("u0", "a1", T0);
        await TrackAsync("u1", null, T0.AddMinutes(1));

        var outcome = await Pipeline().HandleAsync(Identify("u1", "a1", "{}", T0.AddMinutes(2)), CancellationToken.None);

        outcome.Merged.Should().BeFalse();
        outcome.ProfileId.Should().Be(_store.ProfileOf(IdentityKey.ExternalId("u1")).Id);
        _store.ProfileOf(IdentityKey.AnonymousId("a1")).ExternalId.Should().Be("u0");
        await _hooks.DidNotReceive().MergeLocksTakenAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Identify_links_an_anonymous_profile_to_a_user_seen_for_the_first_time()
    {
        await TrackAsync(null, "a1", T0);

        var outcome = await Pipeline().HandleAsync(Identify("u1", "a1", "{}", T0.AddMinutes(1)), CancellationToken.None);

        _store.Profiles.Should().ContainSingle();
        _store.ProfileOf(IdentityKey.ExternalId("u1")).Id.Should().Be(outcome.ProfileId);
        outcome.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task E6_email_and_phone_traits_are_promoted_to_columns_and_identities()
    {
        await Pipeline().HandleAsync(Identify("u1", null, """{"email":" Alice@X.io ","phone":"+62 812-3456-789"}""", T0), CancellationToken.None);

        var profile = _store.ProfileOf(IdentityKey.ExternalId("u1"));
        profile.Email.Should().Be("alice@x.io");
        profile.Phone.Should().Be("+628123456789");
        _store.ProfileOf(new IdentityKey("email", "alice@x.io")).Should().BeSameAs(profile);
        _store.ProfileOf(new IdentityKey("phone", "+628123456789")).Should().BeSameAs(profile);
        profile.Attributes.Should().Contain("Alice@X.io", "traits are stored as sent; only the column is normalized");
    }

    [Fact]
    public async Task E6_an_email_owned_by_another_profile_is_not_promoted_and_nothing_is_merged()
    {
        await Pipeline().HandleAsync(Identify("u1", null, """{"email":"shared@x.io"}""", T0), CancellationToken.None);

        var outcome = await Pipeline().HandleAsync(Identify("u2", null, """{"email":"shared@x.io"}""", T0.AddMinutes(1)), CancellationToken.None);

        _store.Profiles.Should().HaveCount(2);
        _store.Profiles[outcome.ProfileId].Email.Should().BeNull();
        _store.ProfileOf(new IdentityKey("email", "shared@x.io")).ExternalId.Should().Be("u1");
        outcome.Merged.Should().BeFalse();
    }
}
```

Run: `dotnet test tests/Profiles.UnitTests` → build FAILS (`ProfilePipeline`, `ProcessedMessageFactory` missing).

- [ ] **Step 5: Implement `ProcessedMessageFactory` and `ProfilePipeline`**

`Messages/ProcessedMessageFactory.cs`:

```csharp
using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Domain;

namespace Crm.Profiles.Application.Messages;

/// <summary>events.processed payloads. Key {tenantId:N}:{profileId:N} keeps one profile's messages on one partition, in order.</summary>
public static class ProcessedMessageFactory
{
    public static ProcessedMessage EventTracked(EventRecord record, Profile profile)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(profile);
        var payload = new EventTracked(record.Id, profile.TenantId, profile.Id, record.Name, ParseObject(record.Properties), record.OccurredAt, Snapshot(profile));
        return Build(profile.TenantId, profile.Id, payload);
    }

    public static ProcessedMessage ProfileUpdated(Profile profile, IReadOnlyList<string> changedKeys, DateTime occurredAt)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Build(profile.TenantId, profile.Id, new ProfileUpdated(profile.TenantId, profile.Id, changedKeys, Snapshot(profile), occurredAt));
    }

    public static ProcessedMessage ProfileMerged(Guid tenantId, Guid winnerId, Guid loserId, DateTime occurredAt)
        => Build(tenantId, winnerId, new ProfileMerged(tenantId, winnerId, loserId, occurredAt));

    private static ProcessedMessage Build<T>(Guid tenantId, Guid profileId, T payload)
        => new($"{tenantId:N}:{profileId:N}", typeof(T).Name, tenantId, JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options));

    private static ProfileSnapshot Snapshot(Profile profile)
        => new(profile.ExternalId, profile.Email, profile.Phone, ParseObject(profile.Attributes));

    private static JsonElement ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
```

`Pipeline/ProfilePipeline.cs`:

```csharp
using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Domain;

namespace Crm.Profiles.Application.Pipeline;

/// <summary>
/// Spec §4.2 for one message: resolve identities (locking every profile row it touches and following MergedIntoId),
/// merge on identify, apply, append — all inside the store's single transaction — and describe what to publish.
/// </summary>
public sealed class ProfilePipeline(IProfileStore store, IPipelineHooks hooks)
{
    private const string EmailTrait = "email";
    private const string PhoneTrait = "phone";

    public async Task<ProcessingOutcome> HandleAsync(RawMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        await store.BeginAsync(cancellationToken).ConfigureAwait(false);
        var outcome = message switch
        {
            TrackMessage track => await TrackAsync(track.Payload, cancellationToken).ConfigureAwait(false),
            IdentifyMessage identify => await IdentifyAsync(identify.Payload, cancellationToken).ConfigureAwait(false),
            _ => throw new PoisonMessageException($"Unsupported message {message.GetType().Name}."),
        };
        await store.CommitAsync(cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private async Task<ProcessingOutcome> TrackAsync(TrackEventRaw raw, CancellationToken cancellationToken)
    {
        var resolution = new Resolution();
        var target = await ResolveAsync(raw.TenantId, raw.UserId, raw.AnonymousId, raw.ReceivedAt, allowMerge: false, resolution, cancellationToken).ConfigureAwait(false);

        var properties = raw.Properties.ValueKind == JsonValueKind.Object ? raw.Properties.GetRawText() : ProfileAttributes.Empty;
        var record = new EventRecord(raw.EventId, raw.TenantId, target.Id, raw.Event, properties, raw.OccurredAt, raw.ReceivedAt);
        var appended = await store.TryAppendEventAsync(record, cancellationToken).ConfigureAwait(false);
        if (appended)
        {
            target.ApplyTrack(raw.ReceivedAt);
        }

        // Ruling E7: a duplicate is announced again — its first delivery may have died between the SQL commit and the produce.
        return new ProcessingOutcome([ProcessedMessageFactory.EventTracked(record, target)], target.Id, Duplicate: !appended, resolution.Redirected, Merged: false);
    }

    private async Task<ProcessingOutcome> IdentifyAsync(IdentifyRaw raw, CancellationToken cancellationToken)
    {
        var resolution = new Resolution();
        var target = await ResolveAsync(raw.TenantId, raw.UserId, raw.AnonymousId, raw.ReceivedAt, allowMerge: true, resolution, cancellationToken).ConfigureAwait(false);

        var changed = target.ApplyIdentify(raw.Traits, raw.ReceivedAt);
        await PromoteContactPointsAsync(target, raw.Traits, cancellationToken).ConfigureAwait(false);

        var messages = new List<ProcessedMessage>();
        if (resolution.Merge is { } merge)
        {
            messages.Add(ProcessedMessageFactory.ProfileMerged(raw.TenantId, merge.WinnerId, merge.LoserId, raw.ReceivedAt));
        }

        var changedKeys = (resolution.Merge?.GainedKeys ?? []).Concat(changed).Distinct(StringComparer.Ordinal).ToList();
        if (changedKeys.Count > 0 || resolution.Created || resolution.Merge is not null)
        {
            messages.Add(ProcessedMessageFactory.ProfileUpdated(target, changedKeys, raw.ReceivedAt));
        }

        return new ProcessingOutcome(messages, target.Id, Duplicate: false, resolution.Redirected, Merged: resolution.Merge is not null);
    }

    /// <summary>Rulings E5: who the message is about, creating or linking what is missing.</summary>
    private async Task<Profile> ResolveAsync(Guid tenantId, string? userId, string? anonymousId, DateTime receivedAt, bool allowMerge, Resolution resolution, CancellationToken cancellationToken)
    {
        IdentityKey? userKey = string.IsNullOrWhiteSpace(userId) ? null : IdentityKey.ExternalId(userId);
        IdentityKey? anonymousKey = string.IsNullOrWhiteSpace(anonymousId) ? null : IdentityKey.AnonymousId(anonymousId);

        var known = userKey is { } uk ? await FindLockedAsync(uk, resolution, cancellationToken).ConfigureAwait(false) : null;
        var anonymous = anonymousKey is { } ak ? await FindLockedAsync(ak, resolution, cancellationToken).ConfigureAwait(false) : null;

        if (known is not null && anonymous is not null && known.Id != anonymous.Id)
        {
            // Both rows are locked here. A profile that already has an external id is another known user
            // (shared device): never merged, and track never merges at all.
            return allowMerge && anonymous.ExternalId is null
                ? await MergeAsync(known, anonymous, resolution, cancellationToken).ConfigureAwait(false)
                : known;
        }

        if (known is not null)
        {
            if (anonymousKey is { } unmapped && anonymous is null)
            {
                Attach(tenantId, unmapped, known);
            }

            return known;
        }

        if (anonymous is not null && (userKey is null || anonymous.ExternalId is null))
        {
            if (userKey is { } promoteTo)
            {
                anonymous.AssignExternalId(promoteTo.Value);
                Attach(tenantId, promoteTo, anonymous);
            }

            return anonymous;
        }

        // Nobody known yet — or the anonymous id belongs to a different known user, in which case this user starts fresh.
        var created = Profile.Create(tenantId, receivedAt);
        store.Add(created);
        resolution.Created = true;
        if (userKey is { } newUser)
        {
            created.AssignExternalId(newUser.Value);
            Attach(tenantId, newUser, created);
        }

        if (anonymousKey is { } newAnonymous && anonymous is null)
        {
            Attach(tenantId, newAnonymous, created);
        }

        return created;
    }

    private async Task<Profile?> FindLockedAsync(IdentityKey key, Resolution resolution, CancellationToken cancellationToken)
    {
        var profileId = await store.FindProfileIdAsync(key, bypassCache: false, cancellationToken).ConfigureAwait(false);
        if (profileId is null)
        {
            return null;
        }

        var profile = await store.LockProfileAsync(profileId.Value, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            // The cache pointed at a row that does not exist (database rebuilt under a warm Redis).
            store.Forget(key);
            profileId = await store.FindProfileIdAsync(key, bypassCache: true, cancellationToken).ConfigureAwait(false);
            if (profileId is null)
            {
                return null;
            }

            profile = await store.LockProfileAsync(profileId.Value, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Identity {key.Kind}:{key.Value} points to missing profile {profileId}.");
        }

        if (profile.MergedIntoId is null)
        {
            return profile;
        }

        // The id came from before a merge committed. Apply to the winner and repair the cache.
        var winner = await WinnerResolver.ResolveAsync(profile, store.LockProfileAsync, cancellationToken).ConfigureAwait(false);
        resolution.Redirected = true;
        store.Forget(key);
        store.Remember(key, winner.Id);
        return winner;
    }

    private async Task<Profile> MergeAsync(Profile first, Profile second, Resolution resolution, CancellationToken cancellationToken)
    {
        await hooks.MergeLocksTakenAsync(first.TenantId, cancellationToken).ConfigureAwait(false);

        var (winner, loser) = MergePolicy.Decide(first, second);
        var loserIdentities = await store.GetIdentitiesAsync(loser.Id, cancellationToken).ConfigureAwait(false);
        resolution.Merge = ProfileMerge.Apply(winner, loser, loserIdentities);

        foreach (var identity in loserIdentities)
        {
            store.Forget(identity.Key);
        }

        await store.RepointEventsAsync(loser.Id, winner.Id, cancellationToken).ConfigureAwait(false);
        winner.SetEventCount(await store.CountEventsAsync(winner.Id, cancellationToken).ConfigureAwait(false));
        return winner;
    }

    /// <summary>Ruling E6: email/phone become columns and identities only when nobody else owns them.</summary>
    private async Task PromoteContactPointsAsync(Profile target, JsonElement traits, CancellationToken cancellationToken)
    {
        if (traits.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (StringTrait(traits, EmailTrait) is { } email && IdentityKey.Email(email) is { } emailKey
            && await ClaimAsync(target, emailKey, cancellationToken).ConfigureAwait(false))
        {
            target.AssignEmail(emailKey.Value);
        }

        if (StringTrait(traits, PhoneTrait) is { } phone && IdentityKey.Phone(phone) is { } phoneKey
            && await ClaimAsync(target, phoneKey, cancellationToken).ConfigureAwait(false))
        {
            target.AssignPhone(phoneKey.Value);
        }
    }

    private async Task<bool> ClaimAsync(Profile target, IdentityKey key, CancellationToken cancellationToken)
    {
        var owner = await store.FindProfileIdAsync(key, bypassCache: true, cancellationToken).ConfigureAwait(false);
        if (owner is null)
        {
            Attach(target.TenantId, key, target);
            return true;
        }

        return owner == target.Id;
    }

    private void Attach(Guid tenantId, IdentityKey key, Profile profile)
    {
        store.Add(new UserIdentity(tenantId, key, profile.Id));
        store.Remember(key, profile.Id);
    }

    private static string? StringTrait(JsonElement traits, string name)
        => traits.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class Resolution
    {
        public bool Created { get; set; }

        public bool Redirected { get; set; }

        public ProfileMergedEvent? Merge { get; set; }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Profiles.UnitTests` → PASS. `dotnet build Crm.sln -warnaserror` → 0 warnings. `dotnet test tests/Crm.ArchTests` → green.

Note for the test `IT002_shape…`: `Attributes.Should().Be("""{"plan":"free","city":"SG"}""")` relies on `JsonObject` keeping insertion order and compact output — it does; if the assertion fails on whitespace, compare parsed JSON instead and say so.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Profiles/Profiles.Application tests/Profiles.UnitTests
git commit -m "feat(profiles): per-message pipeline — resolve, merge, apply, append — with decoder, processed messages and retry policy

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Infrastructure — EF + Redis profile store, module registration

**Files:**
- Create: `src/Modules/Profiles/Profiles.Infrastructure/Caching/IIdentityCache.cs`, `RedisIdentityCache.cs`, `src/Modules/Profiles/Profiles.Infrastructure/Persistence/SqlErrors.cs`, `EfProfileStore.cs`
- Modify: `src/Modules/Profiles/Profiles.Infrastructure/ProfilesModule.cs`, `Profiles.Infrastructure.csproj` (`InternalsVisibleTo`), `tests/Profiles.UnitTests/Profiles.UnitTests.csproj` (reference Infrastructure)
- Test: `tests/Profiles.UnitTests/RedisIdentityCacheTests.cs`, `tests/Crm.IntegrationTests/Profiles/ProfileStoreTests.cs`

**Interfaces:**
- Consumes: `IProfileStore`, `ProfilePipeline`, `IPipelineHooks`, `NoPipelineHooks` (Task 3); `ProfilesDbContext` (Task 2); `Crm.Kernel.Redis.RedisKeys.IdentityCache(Guid tenantId, string kind, string value)`; `IConnectionMultiplexer` and `IClock`, `ITenantContext`, `TenantSaveChangesInterceptor` — all registered by the **host**, not by the module.
- Produces: `AddProfilesModule` additionally registers `IPipelineHooks` → `NoPipelineHooks` (singleton, `TryAdd`), `IIdentityCache` → `RedisIdentityCache` (singleton), `IProfileStore` → `EfProfileStore` (scoped), `ProfilePipeline` (scoped). `internal interface IIdentityCache { Task<Guid?> GetAsync(Guid tenantId, IdentityKey key); Task SetAsync(Guid tenantId, IdentityKey key, Guid profileId); Task RemoveAsync(Guid tenantId, IReadOnlyCollection<IdentityKey> keys); }`. Cache value: profile id as 32 hex characters, TTL 1 h.

- [ ] **Step 1: Project plumbing**

`Profiles.Infrastructure.csproj` — add:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Profiles.UnitTests" />
    <InternalsVisibleTo Include="DynamicProxyGenAssembly2" />
  </ItemGroup>
```

(`DynamicProxyGenAssembly2` lets NSubstitute proxy internal interfaces.) `tests/Profiles.UnitTests/Profiles.UnitTests.csproj` — add next to the existing `ProjectReference`:

```xml
    <ProjectReference Include="..\..\src\Modules\Profiles\Profiles.Infrastructure\Profiles.Infrastructure.csproj" />
```

- [ ] **Step 2: Write the failing tests**

`tests/Profiles.UnitTests/RedisIdentityCacheTests.cs`:

```csharp
using Crm.Kernel.Redis;
using Crm.Profiles.Domain;
using Crm.Profiles.Infrastructure.Caching;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class RedisIdentityCacheTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly IdentityKey Key = IdentityKey.ExternalId("u1");
    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly RedisIdentityCache _cache;

    public RedisIdentityCacheTests()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(_database);
        _cache = new RedisIdentityCache(multiplexer, NullLogger<RedisIdentityCache>.Instance);
    }

    [Fact]
    public async Task UT057_the_key_is_tenant_prefixed_and_the_value_is_the_profile_id_in_hex()
    {
        var profileId = Guid.CreateVersion7();
        _database.StringGetAsync(RedisKeys.IdentityCache(Tenant, Key.Kind, Key.Value), Arg.Any<CommandFlags>()).Returns(new RedisValue(profileId.ToString("N")));

        (await _cache.GetAsync(Tenant, Key)).Should().Be(profileId);
    }

    [Fact]
    public async Task A_miss_and_an_unparsable_value_are_both_null()
    {
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null, new RedisValue("not-a-guid"));

        (await _cache.GetAsync(Tenant, Key)).Should().BeNull();
        (await _cache.GetAsync(Tenant, Key)).Should().BeNull();
    }

    [Fact]
    public async Task E12_redis_failures_degrade_to_a_miss_and_never_throw()
    {
        _database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));
        _database.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).ThrowsAsync(new RedisTimeoutException("slow", CommandStatus.Unknown));

        (await _cache.GetAsync(Tenant, Key)).Should().BeNull();
        await _cache.Invoking(c => c.RemoveAsync(Tenant, [Key])).Should().NotThrowAsync();
        await _cache.Invoking(c => c.SetAsync(Tenant, Key, Guid.CreateVersion7())).Should().NotThrowAsync();
    }
}
```

If the `StringSetAsync` overload the implementation calls differs from what NSubstitute needs to stub in the last test, leave `SetAsync` un-stubbed (a substitute returns a completed default task) — the point of the test is that nothing throws.

`tests/Crm.IntegrationTests/Profiles/ProfileStoreTests.cs`:

```csharp
using System.Text.Json;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using Crm.Kernel.Data;
using Crm.Kernel.Redis;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

/// <summary>The store against real SQL Server and Redis, without Kafka and without a host: the module plus the services a host provides.</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ProfileStoreTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
    private readonly CrmStackFixture _stack;
    private readonly ServiceProvider _provider;
    private readonly Guid _tenant = Guid.CreateVersion7();

    public ProfileStoreTests(CrmStackFixture stack)
    {
        _stack = stack;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Crm"] = stack.SqlConnectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(stack.RedisConfiguration));
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSaveChangesInterceptor>();
        services.AddProfilesModule(configuration);
        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private AsyncServiceScope Scope(Guid tenant)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(tenant, null, TenantPrincipalKind.None);
        return scope;
    }

    private static TrackMessage Track(Guid tenant, string userId, Guid eventId, DateTime at)
        => new(new TrackEventRaw(eventId, tenant, userId, null, "purchase", JsonDocument.Parse("""{"amount":1}""").RootElement.Clone(), at, at));

    private async Task<ProcessingOutcome> HandleAsync(Guid tenant, RawMessage message)
    {
        await using var scope = Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<ProfilePipeline>().HandleAsync(message, CancellationToken.None);
    }

    [Fact]
    public async Task A_track_is_persisted_once_however_often_it_is_delivered()
    {
        var eventId = Guid.CreateVersion7();
        var occurredWithTicks = T0.AddTicks(1_234_567); // more precise than datetime2(3): ruling E4

        var first = await HandleAsync(_tenant, Track(_tenant, "u1", eventId, occurredWithTicks));
        var second = await HandleAsync(_tenant, Track(_tenant, "u1", eventId, occurredWithTicks));

        first.Duplicate.Should().BeFalse();
        second.Duplicate.Should().BeTrue();
        second.ProfileId.Should().Be(first.ProfileId);
        await using var scope = Scope(_tenant);
        var db = scope.ServiceProvider.GetRequiredService<ProfilesDbContext>();
        (await db.Events.CountAsync()).Should().Be(1);
        var profile = await db.Profiles.SingleAsync();
        profile.EventCount.Should().Be(1);
        profile.ExternalId.Should().Be("u1");
        profile.Attributes.Should().Be("{}");
        profile.CreatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
        (await db.UserIdentities.SingleAsync()).ProfileId.Should().Be(profile.Id);
    }

    [Fact]
    public async Task The_identity_is_cached_after_the_commit_with_a_one_hour_ttl()
    {
        var outcome = await HandleAsync(_tenant, Track(_tenant, "cached-user", Guid.CreateVersion7(), T0));

        var redis = _provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var key = RedisKeys.IdentityCache(_tenant, "external_id", "cached-user");
        (await redis.StringGetAsync(key)).ToString().Should().Be(outcome.ProfileId.ToString("N"));
        var ttl = await redis.KeyTimeToLiveAsync(key);
        ttl.Should().NotBeNull();
        ttl!.Value.Should().BeCloseTo(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_rolled_back_message_leaves_no_rows_and_no_cache_entry()
    {
        await using (var scope = Scope(_tenant))
        {
            var store = scope.ServiceProvider.GetRequiredService<IProfileStore>();
            await store.BeginAsync(CancellationToken.None);
            var profile = Crm.Profiles.Domain.Profile.Create(_tenant, T0);
            store.Add(profile);
            store.Remember(Crm.Profiles.Domain.IdentityKey.ExternalId("ghost"), profile.Id);
            // no CommitAsync: disposing the scope rolls back
        }

        await using var verify = Scope(_tenant);
        (await verify.ServiceProvider.GetRequiredService<ProfilesDbContext>().Profiles.CountAsync()).Should().Be(0);
        var redis = _provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        (await redis.KeyExistsAsync(RedisKeys.IdentityCache(_tenant, "external_id", "ghost"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_locked_profile_blocks_a_second_locker_until_the_first_commits()
    {
        var profileId = (await HandleAsync(_tenant, Track(_tenant, "locked-user", Guid.CreateVersion7(), T0))).ProfileId;

        await using var first = Scope(_tenant);
        var holder = first.ServiceProvider.GetRequiredService<IProfileStore>();
        await holder.BeginAsync(CancellationToken.None);
        (await holder.LockProfileAsync(profileId, CancellationToken.None)).Should().NotBeNull();

        await using var second = Scope(_tenant);
        var waiter = second.ServiceProvider.GetRequiredService<IProfileStore>();
        await waiter.BeginAsync(CancellationToken.None);
        var waiting = waiter.LockProfileAsync(profileId, CancellationToken.None);

        (await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(waiting, "UPDLOCK, HOLDLOCK must serialize writers of one profile");
        await holder.CommitAsync(CancellationToken.None);
        (await waiting.WaitAsync(TimeSpan.FromSeconds(30))).Should().NotBeNull();
        await waiter.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NFR03_another_tenant_cannot_lock_or_find_the_profile()
    {
        var outcome = await HandleAsync(_tenant, Track(_tenant, "tenant-a-user", Guid.CreateVersion7(), T0));
        var otherTenant = Guid.CreateVersion7();

        await using var scope = Scope(otherTenant);
        var store = scope.ServiceProvider.GetRequiredService<IProfileStore>();
        await store.BeginAsync(CancellationToken.None);

        (await store.LockProfileAsync(outcome.ProfileId, CancellationToken.None)).Should().BeNull();
        (await store.FindProfileIdAsync(Crm.Profiles.Domain.IdentityKey.ExternalId("tenant-a-user"), bypassCache: false, CancellationToken.None)).Should().BeNull();
    }
}
```

Run: unit tests → build FAILS (`RedisIdentityCache` missing); integration class → FAILS (`IProfileStore` not registered).

- [ ] **Step 3: Implement cache, store and registration**

`Caching/IIdentityCache.cs`:

```csharp
using Crm.Profiles.Domain;

namespace Crm.Profiles.Infrastructure.Caching;

/// <summary>t:{tenantId}:id:{kind}:{value} → profile id. Rebuildable from profiles.UserIdentities; never authoritative (ADR-0006).</summary>
internal interface IIdentityCache
{
    Task<Guid?> GetAsync(Guid tenantId, IdentityKey key);

    Task SetAsync(Guid tenantId, IdentityKey key, Guid profileId);

    Task RemoveAsync(Guid tenantId, IReadOnlyCollection<IdentityKey> keys);
}
```

`Caching/RedisIdentityCache.cs`:

```csharp
using Crm.Kernel.Redis;
using Crm.Profiles.Domain;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Crm.Profiles.Infrastructure.Caching;

/// <summary>Ruling E12: a Redis problem is a cache miss, never a failed message.</summary>
internal sealed class RedisIdentityCache(IConnectionMultiplexer redis, ILogger<RedisIdentityCache> logger) : IIdentityCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    public async Task<Guid?> GetAsync(Guid tenantId, IdentityKey key)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(RedisKeys.IdentityCache(tenantId, key.Kind, key.Value)).ConfigureAwait(false);
            return value.HasValue && Guid.TryParseExact(value.ToString(), "N", out var profileId) ? profileId : null;
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            logger.LogWarning(ex, "Identity cache read failed; falling back to SQL");
            return null;
        }
    }

    public async Task SetAsync(Guid tenantId, IdentityKey key, Guid profileId)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(RedisKeys.IdentityCache(tenantId, key.Kind, key.Value), profileId.ToString("N"), Ttl).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            logger.LogWarning(ex, "Identity cache write failed; the next lookup reads SQL");
        }
    }

    public async Task RemoveAsync(Guid tenantId, IReadOnlyCollection<IdentityKey> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        try
        {
            var redisKeys = keys.Select(k => (RedisKey)RedisKeys.IdentityCache(tenantId, k.Kind, k.Value)).ToArray();
            await redis.GetDatabase().KeyDeleteAsync(redisKeys).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRedisFailure(ex))
        {
            // A stale entry that survives points at a merge loser; the applier follows MergedIntoId, so it is slow, not wrong.
            logger.LogWarning(ex, "Identity cache delete failed; stale entries expire within {Ttl}", Ttl);
        }
    }

    private static bool IsRedisFailure(Exception ex) => ex is RedisException or TimeoutException;
}
```

`Persistence/SqlErrors.cs`:

```csharp
using Microsoft.Data.SqlClient;

namespace Crm.Profiles.Infrastructure.Persistence;

internal static class SqlErrors
{
    /// <summary>2601: duplicate key row in a unique index. 2627: unique or primary-key constraint violation.</summary>
    public static bool IsUniqueViolation(SqlException exception) => exception.Number is 2601 or 2627;
}
```

`Persistence/EfProfileStore.cs`:

```csharp
using System.Data;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Domain;
using Crm.Profiles.Infrastructure.Caching;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Crm.Profiles.Infrastructure.Persistence;

/// <summary>
/// One message, one transaction (READ COMMITTED; profile rows are taken WITH (UPDLOCK, HOLDLOCK), spec §4.2).
/// Entities go through EF so the tenant interceptor stamps and guards them; the statements EF cannot express
/// (lock hints, insert-if-absent, set-based re-point) are interpolated SQL on the same connection and transaction.
/// Disposing without <see cref="CommitAsync"/> rolls back.
/// </summary>
internal sealed class EfProfileStore(ProfilesDbContext db, IIdentityCache cache, ITenantContext tenant, IClock clock) : IProfileStore, IAsyncDisposable
{
    private readonly Dictionary<IdentityKey, Guid> _pendingSets = [];
    private readonly HashSet<IdentityKey> _pendingDeletes = [];
    private IDbContextTransaction? _transaction;

    public async Task BeginAsync(CancellationToken cancellationToken)
        => _transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

    public async Task<Guid?> FindProfileIdAsync(IdentityKey key, bool bypassCache, CancellationToken cancellationToken)
    {
        // Added in this transaction and not flushed yet.
        if (db.UserIdentities.Local.FirstOrDefault(i => i.Kind == key.Kind && i.Value == key.Value) is { } local)
        {
            return local.ProfileId;
        }

        if (!bypassCache && await cache.GetAsync(tenant.TenantId, key).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        var found = await db.UserIdentities.AsNoTracking()
            .Where(i => i.Kind == key.Kind && i.Value == key.Value)
            .Select(i => (Guid?)i.ProfileId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (found is { } profileId && !_pendingDeletes.Contains(key))
        {
            _pendingSets[key] = profileId;
        }

        return found;
    }

    public async Task<Profile?> LockProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        if (db.Profiles.Local.FirstOrDefault(p => p.Id == profileId) is { } tracked)
        {
            return tracked; // created or already locked by this transaction
        }

        var tenantId = tenant.TenantId;
        return await db.Profiles
            .FromSqlInterpolated($"SELECT * FROM profiles.Profiles WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = {tenantId} AND Id = {profileId}")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserIdentity>> GetIdentitiesAsync(Guid profileId, CancellationToken cancellationToken)
        => await db.UserIdentities.Where(i => i.ProfileId == profileId).ToListAsync(cancellationToken).ConfigureAwait(false);

    public void Add(Profile profile) => db.Profiles.Add(profile);

    public void Add(UserIdentity identity) => db.UserIdentities.Add(identity);

    public async Task<bool> TryAppendEventAsync(EventRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var tenantId = tenant.TenantId;
        if (record.TenantId != tenantId)
        {
            throw new TenantMismatchException(tenantId, record.TenantId, nameof(EventRecord));
        }

        // A profile created by this message must exist before FK_Events_ProfileId is checked.
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var now = EventTime.TruncateToMilliseconds(clock.UtcNow);
        try
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO profiles.Events (Id, TenantId, ProfileId, Name, Properties, OccurredAt, ReceivedAt, CreatedAt, UpdatedAt)
                SELECT {record.Id}, {tenantId}, {record.ProfileId}, {record.Name}, {record.Properties}, {record.OccurredAt}, {record.ReceivedAt}, {now}, {now}
                WHERE NOT EXISTS (
                    SELECT 1 FROM profiles.Events WITH (UPDLOCK, HOLDLOCK)
                    WHERE Id = {record.Id} AND OccurredAt = {record.OccurredAt})
                """,
                cancellationToken).ConfigureAwait(false);
            return inserted == 1;
        }
        catch (SqlException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            // Lost a race against another delivery of the same record. A unique violation ends the statement, not the transaction.
            return false;
        }
    }

    public async Task RepointEventsAsync(Guid loserId, Guid winnerId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var now = EventTime.TruncateToMilliseconds(clock.UtcNow);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE profiles.Events SET ProfileId = {winnerId}, UpdatedAt = {now} WHERE TenantId = {tenantId} AND ProfileId = {loserId}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> CountEventsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return await db.Database
            .SqlQuery<long>($"SELECT COUNT_BIG(*) AS [Value] FROM profiles.Events WHERE TenantId = {tenantId} AND ProfileId = {profileId}")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Remember(IdentityKey key, Guid profileId) => _pendingSets[key] = profileId;

    public void Forget(IdentityKey key)
    {
        _pendingSets.Remove(key);
        _pendingDeletes.Add(key);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        var transaction = _transaction ?? throw new InvalidOperationException("BeginAsync was not called.");

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Spec §4.2 item 3: a merge loser's cache entries go before the commit, so nobody can read them afterwards.
        await cache.RemoveAsync(tenant.TenantId, _pendingDeletes).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (key, profileId) in _pendingSets)
        {
            await cache.SetAsync(tenant.TenantId, key, profileId).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }
    }
}
```

Two things to watch while making the tests pass:
- A merge saves the loser (`ExternalId` → `NULL`) and the winner (`ExternalId` → the same value) in one `SaveChanges`. EF Core orders commands so a unique-index value is released before it is taken. If Task 7's merge test ever reports a violation of `UX_Profiles_TenantId_ExternalId`, the fix belongs here: save loser rows first (`db.ChangeTracker.Entries<Profile>()` with a modified `MergedIntoId`), then the rest — not in the domain.
- `TenantMismatchException`'s constructor is `(Guid expected, Guid actual, string entityName)` (see `TenantSaveChangesInterceptor`).

`ProfilesModule.cs` — after the `AddDbContext` call, before `return services;`:

```csharp
        services.TryAddSingleton<IPipelineHooks, NoPipelineHooks>();
        services.AddSingleton<IIdentityCache, RedisIdentityCache>();
        services.AddScoped<IProfileStore, EfProfileStore>();
        services.AddScoped<ProfilePipeline>();
```

with `using Crm.Profiles.Application.Pipeline;`, `using Crm.Profiles.Infrastructure.Caching;`, `using Crm.Profiles.Infrastructure.Persistence;`, `using Microsoft.Extensions.DependencyInjection.Extensions;`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet build Crm.sln -warnaserror` → 0 warnings; `dotnet test Crm.sln --filter "Category!=Integration"` → green (the API host still builds its container: `IConnectionMultiplexer` comes from `AddCrmHealthChecks`); then with the environment prefix `dotnet test Crm.sln --filter "Category=Integration&FullyQualifiedName~ProfileStoreTests"` → 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Profiles/Profiles.Infrastructure tests/Profiles.UnitTests tests/Crm.IntegrationTests/Profiles/ProfileStoreTests.cs
git commit -m "feat(profiles): EF + Redis profile store — row locks, idempotent event insert, cache writes after commit

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Worker runtime — consumer, publishers, DLQ, metrics, schema gate, shared test Worker

**Files:**
- Create: `src/Modules/Profiles/Profiles.Infrastructure/Consumers/ProfilesConsumerOptions.cs`, `ScopedMessageHandler.cs`, `SchemaGate.cs`, `RawEventsConsumerService.cs`
- Create: `src/Modules/Profiles/Profiles.Infrastructure/Kafka/WorkerProducer.cs`, `IProcessedPublisher.cs`, `ProcessedEventPublisher.cs`, `IDeadLetterPublisher.cs`, `DeadLetterMessageFactory.cs`, `DeadLetterPublisher.cs`
- Create: `src/Modules/Profiles/Profiles.Infrastructure/ProfilesMetrics.cs`, `ProfilesWorkerRegistration.cs`
- Modify: `src/Modules/Profiles/Profiles.Infrastructure/Profiles.Infrastructure.csproj` (`Confluent.Kafka`), `src/Hosts/Crm.Worker/Program.cs`, `StartupProbeService.cs` (doc comment), `appsettings.json`
- Test (unit): `tests/Profiles.UnitTests/DeadLetterMessageFactoryTests.cs`, `ProfilesMetricsTests.cs`, `ProfilesConsumerOptionsTests.cs`
- Test (integration): create `tests/Crm.IntegrationTests/Fixtures/CrmWorkerFactory.cs`, `SwitchableHooks.cs`, `Eventually.cs`, `ProfilesDb.cs`, `TopicReader.cs`; modify `Fixtures/CrmStackFixture.cs`; create `tests/Crm.IntegrationTests/Ingestion/TrackPipelineTests.cs`

**Interfaces:**
- Consumes: `IRawMessageHandler`, `IPipelineHooks`, `RawEnvelope`, `ProcessedMessage`, `ProcessingOutcome`, `RawMessageDecoder`, `ProfilePipeline`, `MessageRetryPolicy` (Task 3); `AddProfilesModule` registrations (Task 4); `Crm.Contracts.KafkaTopics`, `KafkaHeaders`; host-registered `TenantContext` (concrete, scoped).
- Produces:
  - `IServiceCollection AddProfilesWorker(this IServiceCollection, IConfiguration)` in `Crm.Profiles.Infrastructure` — called by the **Worker only**.
  - `public sealed class ProfilesConsumerOptions` — section `Profiles:Consumer`: `Enabled` (true), `GroupId` (`profiles`), `Instances` (4), `RetryDelaysMs` (`[]` = 200, 2000), `RestartDelayMs` (1000), `BootstrapServers` (filled from `Kafka:BootstrapServers`); `IReadOnlyList<TimeSpan> RetryDelays`.
  - `public sealed class ProfilesMetrics` — meter `Crm.Profiles`; exported `crm_dlq_total{topic}` and `crm_consumer_lag{topic,partition}`.
  - Test fixtures: `CrmStackFixture.Worker` (`CrmWorkerFactory`, started once per run), `CrmStackFixture.Hooks` (`SwitchableHooks`), `Eventually.TrueAsync/GetAsync`, `ProfilesDb` (`FindByIdentityAsync`, `GetAsync`, `EventsAsync`, `CountProfilesAsync`, records `ProfileRow`, `EventRow`), `TopicReader.ReadTenantAsync` (record `TopicRecord`).

- [ ] **Step 1: Write the failing unit tests**

`tests/Profiles.UnitTests/DeadLetterMessageFactoryTests.cs`:

```csharp
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Infrastructure.Kafka;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class DeadLetterMessageFactoryTests
{
    private static readonly RawEnvelope Envelope = new(
        KafkaTopics.EventsRaw, 7, 1234, "tenant:u1", [1, 2, 3],
        new Dictionary<string, string> { [KafkaHeaders.TenantId] = "abc", [KafkaHeaders.MessageType] = "TrackEventRaw" });

    [Fact]
    public void FR07_original_bytes_key_and_headers_are_kept_and_the_failure_is_described()
    {
        var failure = Capture(() => throw new PoisonMessageException("bad json"));

        var letter = DeadLetterMessageFactory.Create(Envelope, failure, attempts: 3);

        letter.Key.Should().Be("tenant:u1");
        letter.Value.Should().Equal(1, 2, 3);
        letter.Headers[KafkaHeaders.TenantId].Should().Be("abc");
        letter.Headers[KafkaHeaders.MessageType].Should().Be("TrackEventRaw");
        letter.Headers[KafkaHeaders.DlqError].Should().Be("PoisonMessageException: bad json");
        letter.Headers[KafkaHeaders.DlqStack].Should().Contain(nameof(DeadLetterMessageFactoryTests));
        letter.Headers[KafkaHeaders.DlqAttempt].Should().Be("3");
        letter.Headers[KafkaHeaders.DlqSourceTopic].Should().Be(KafkaTopics.EventsRaw);
        letter.Headers[KafkaHeaders.DlqSourcePartition].Should().Be("7");
        letter.Headers[KafkaHeaders.DlqSourceOffset].Should().Be("1234");
    }

    [Fact]
    public void Header_values_are_bounded_so_a_huge_stack_cannot_exceed_the_broker_message_limit()
    {
        var failure = new InvalidOperationException(new string('x', 10_000));

        var letter = DeadLetterMessageFactory.Create(Envelope, failure, attempts: 3);

        letter.Headers[KafkaHeaders.DlqError].Length.Should().BeLessThanOrEqualTo(1024);
        letter.Headers[KafkaHeaders.DlqStack].Length.Should().BeLessThanOrEqualTo(8192);
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
        }
        catch (PoisonMessageException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("the action did not throw");
    }
}
```

`tests/Profiles.UnitTests/ProfilesMetricsTests.cs`:

```csharp
using System.Diagnostics.Metrics;
using Crm.Profiles.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ProfilesMetricsTests
{
    [Fact]
    public void Dead_letters_are_counted_per_source_topic_and_lag_is_observed_per_partition()
    {
        using var metrics = new ProfilesMetrics();
        var measurements = new List<(string Instrument, long Value, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ProfilesMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}")))));
        listener.Start();

        metrics.DeadLettered("events.raw");
        metrics.SetLag("events.raw", 3, 17);
        metrics.SetLag("events.raw", 4, 0);
        metrics.ClearLag("events.raw", 4);
        listener.RecordObservableInstruments();

        // The Prometheus exporter appends _total to counters, so the instrument is named without it.
        measurements.Should().Contain(("crm_dlq", 1L, "topic=events.raw"));
        measurements.Should().Contain(("crm_consumer_lag", 17L, "topic=events.raw,partition=3"));
        measurements.Should().NotContain(m => m.Tags.EndsWith("partition=4", StringComparison.Ordinal));
    }
}
```

`tests/Profiles.UnitTests/ProfilesConsumerOptionsTests.cs`:

```csharp
using Crm.Profiles.Infrastructure.Consumers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class ProfilesConsumerOptionsTests
{
    [Fact]
    public void Defaults_are_the_spec_values()
    {
        var options = new ProfilesConsumerOptions();

        options.Enabled.Should().BeTrue();
        options.GroupId.Should().Be("profiles");
        options.Instances.Should().Be(4);
        options.RetryDelays.Should().Equal(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Configured_delays_replace_the_defaults_instead_of_being_appended_to_them()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Profiles:Consumer:RetryDelaysMs:0"] = "50",
            ["Profiles:Consumer:RetryDelaysMs:1"] = "100",
        }).Build();
        var options = new ProfilesConsumerOptions();

        configuration.GetSection(ProfilesConsumerOptions.SectionName).Bind(options);

        options.RetryDelays.Should().Equal(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100));
    }
}
```

Run: `dotnet test tests/Profiles.UnitTests` → build FAILS (types missing).

- [ ] **Step 2: Options, metrics, dead-letter factory**

`Profiles.Infrastructure.csproj` — add `<PackageReference Include="Confluent.Kafka" />`.

`Consumers/ProfilesConsumerOptions.cs`:

```csharp
using Crm.Contracts;

namespace Crm.Profiles.Infrastructure.Consumers;

public sealed class ProfilesConsumerOptions
{
    public const string SectionName = "Profiles:Consumer";

    private static readonly int[] DefaultRetryDelaysMs = [200, 2000];

    /// <summary>False keeps the services registered but never subscribes (tooling, tests that only need the pipeline).</summary>
    public bool Enabled { get; set; } = true;

    public string GroupId { get; set; } = KafkaTopics.ProfilesConsumerGroup;

    /// <summary>Consumers in this process. Kafka spreads the partitions over them; each one processes sequentially (ruling E10).</summary>
    public int Instances { get; set; } = 4;

    /// <summary>
    /// Waits between attempts; attempts = length + 1. Empty means the spec default (200 ms, 2 s). The default is not
    /// the initial value because the configuration binder appends to a non-empty array instead of replacing it.
    /// </summary>
    public int[] RetryDelaysMs { get; set; } = [];

    /// <summary>Pause before a consumer is rebuilt after a failure outside message processing (ruling E9).</summary>
    public int RestartDelayMs { get; set; } = 1000;

    /// <summary>Filled from Kafka:BootstrapServers by the registration.</summary>
    public string BootstrapServers { get; set; } = string.Empty;

    public IReadOnlyList<TimeSpan> RetryDelays
        => (RetryDelaysMs.Length > 0 ? RetryDelaysMs : DefaultRetryDelaysMs).Select(ms => TimeSpan.FromMilliseconds(ms)).ToList();
}
```

If CA1819 objects to the array property, use `List<int>` (the binder replaces nothing there either, so keep the empty default) and say so.

`ProfilesMetrics.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Crm.Profiles.Infrastructure;

/// <summary>
/// Spec §8.2: crm_dlq_total{topic} and crm_consumer_lag. The Prometheus exporter appends `_total` to counters, so the
/// counter instrument is `crm_dlq`; gauges are exported under their own name.
/// </summary>
public sealed class ProfilesMetrics : IDisposable
{
    public const string MeterName = "Crm.Profiles";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _deadLettered;
    private readonly ConcurrentDictionary<(string Topic, int Partition), long> _lag = new();

    public ProfilesMetrics()
    {
        _deadLettered = _meter.CreateCounter<long>("crm_dlq", description: "Messages moved to events.dlq, by source topic.");
        _meter.CreateObservableGauge("crm_consumer_lag", ObserveLag, description: "High watermark minus consumer position, per assigned partition.");
    }

    public void DeadLettered(string topic) => _deadLettered.Add(1, new KeyValuePair<string, object?>("topic", topic));

    public void SetLag(string topic, int partition, long lag) => _lag[(topic, partition)] = lag;

    /// <summary>A revoked partition is someone else's to report.</summary>
    public void ClearLag(string topic, int partition) => _lag.TryRemove((topic, partition), out _);

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<long>> ObserveLag()
        => _lag.Select(entry => new Measurement<long>(
            entry.Value,
            new KeyValuePair<string, object?>("topic", entry.Key.Topic),
            new KeyValuePair<string, object?>("partition", entry.Key.Partition)));
}
```

`Kafka/DeadLetterMessageFactory.cs`:

```csharp
using System.Globalization;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Infrastructure.Kafka;

internal sealed record DeadLetter(string? Key, byte[] Value, IReadOnlyDictionary<string, string> Headers);

/// <summary>FR-07: the original record, untouched, plus why and where it failed.</summary>
internal static class DeadLetterMessageFactory
{
    private const int MaxErrorLength = 1024;
    private const int MaxStackLength = 8192;

    public static DeadLetter Create(RawEnvelope envelope, Exception failure, int attempts)
    {
        var headers = new Dictionary<string, string>(envelope.Headers, StringComparer.Ordinal)
        {
            [KafkaHeaders.DlqError] = Truncate($"{failure.GetType().Name}: {failure.Message}", MaxErrorLength),
            [KafkaHeaders.DlqStack] = Truncate(failure.ToString(), MaxStackLength),
            [KafkaHeaders.DlqAttempt] = attempts.ToString(CultureInfo.InvariantCulture),
            [KafkaHeaders.DlqSourceTopic] = envelope.Topic,
            [KafkaHeaders.DlqSourcePartition] = envelope.Partition.ToString(CultureInfo.InvariantCulture),
            [KafkaHeaders.DlqSourceOffset] = envelope.Offset.ToString(CultureInfo.InvariantCulture),
        };
        return new DeadLetter(envelope.Key, envelope.Value, headers);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
```

Run: `dotnet test tests/Profiles.UnitTests` → PASS.

- [ ] **Step 3: Producers**

`Kafka/WorkerProducer.cs`:

```csharp
using System.Text;
using Confluent.Kafka;
using Crm.Profiles.Infrastructure.Consumers;
using Microsoft.Extensions.Options;

namespace Crm.Profiles.Infrastructure.Kafka;

/// <summary>One idempotent acks=all producer for events.processed and events.dlq. Built on first use; a construction failure is not cached.</summary>
internal sealed class WorkerProducer(IOptions<ProfilesConsumerOptions> options) : IDisposable
{
    private readonly Lock _gate = new();
    private IProducer<string, byte[]>? _producer;

    public Task ProduceAsync(string topic, string? key, byte[] value, IEnumerable<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        var kafkaHeaders = new Headers();
        foreach (var (name, text) in headers)
        {
            kafkaHeaders.Add(name, Encoding.UTF8.GetBytes(text));
        }

        // Key is null for a dead letter whose source had none; the serializer writes a null key.
        return Get().ProduceAsync(topic, new Message<string, byte[]> { Key = key!, Value = value, Headers = kafkaHeaders }, cancellationToken);
    }

    public void Dispose()
    {
        if (_producer is { } producer)
        {
            producer.Flush(TimeSpan.FromSeconds(5));
            producer.Dispose();
        }
    }

    private IProducer<string, byte[]> Get()
    {
        if (_producer is { } existing)
        {
            return existing;
        }

        lock (_gate)
        {
            return _producer ??= new ProducerBuilder<string, byte[]>(new ProducerConfig
            {
                BootstrapServers = options.Value.BootstrapServers,
                ClientId = "crm-worker",
                Acks = Acks.All,
                EnableIdempotence = true,
                LingerMs = 5,
            }).Build();
        }
    }
}
```

`Kafka/IProcessedPublisher.cs` and `Kafka/IDeadLetterPublisher.cs`:

```csharp
using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Infrastructure.Kafka;

internal interface IProcessedPublisher
{
    /// <summary>Returns once Kafka acknowledged the record. Retries in place until then (ruling E7); only shutdown ends it early.</summary>
    Task PublishAsync(ProcessedMessage message, CancellationToken cancellationToken);
}
```

```csharp
using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Infrastructure.Kafka;

internal interface IDeadLetterPublisher
{
    /// <summary>One attempt; a failure propagates so the consumer is rebuilt and the record redelivered (ruling E9).</summary>
    Task PublishAsync(RawEnvelope envelope, Exception failure, int attempts, CancellationToken cancellationToken);
}
```

`Kafka/ProcessedEventPublisher.cs`:

```csharp
using System.Diagnostics;
using Confluent.Kafka;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Microsoft.Extensions.Logging;

namespace Crm.Profiles.Infrastructure.Kafka;

internal sealed class ProcessedEventPublisher(WorkerProducer producer, ILogger<ProcessedEventPublisher> logger) : IProcessedPublisher
{
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(1);

    public async Task PublishAsync(ProcessedMessage message, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KafkaHeaders.TenantId] = message.TenantId.ToString("N"),
            [KafkaHeaders.MessageType] = message.MessageType,
            [KafkaHeaders.ContentType] = KafkaHeaders.JsonContentType,
        };
        if (Activity.Current?.Id is { } traceParent)
        {
            headers[KafkaHeaders.TraceParent] = traceParent;
        }

        while (true)
        {
            try
            {
                await producer.ProduceAsync(KafkaTopics.EventsProcessed, message.Key, message.Value, headers, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (KafkaException ex)
            {
                // SQL is committed. Giving up would lose ProfileUpdated/ProfileMerged for good (only EventTracked is re-announced on redelivery).
                logger.LogWarning(ex, "Publishing {MessageType} to {Topic} failed: {Reason}; retrying", message.MessageType, KafkaTopics.EventsProcessed, ex.Error.Reason);
                await Task.Delay(RetryPause, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
```

`Kafka/DeadLetterPublisher.cs`:

```csharp
using Crm.Contracts;
using Crm.Profiles.Application.Messages;

namespace Crm.Profiles.Infrastructure.Kafka;

internal sealed class DeadLetterPublisher(WorkerProducer producer) : IDeadLetterPublisher
{
    public Task PublishAsync(RawEnvelope envelope, Exception failure, int attempts, CancellationToken cancellationToken)
    {
        var letter = DeadLetterMessageFactory.Create(envelope, failure, attempts);
        return producer.ProduceAsync(KafkaTopics.EventsDlq, letter.Key, letter.Value, letter.Headers, cancellationToken);
    }
}
```

- [ ] **Step 4: Scoped handler, schema gate, consumer service, registration**

`Consumers/ScopedMessageHandler.cs`:

```csharp
using Crm.Kernel.Tenancy;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crm.Profiles.Infrastructure.Consumers;

/// <summary>Spec §2.5: consumers create a tenant scope per message. One call = one attempt = one DI scope = one SQL transaction.</summary>
internal sealed class ScopedMessageHandler(IServiceScopeFactory scopes, ILogger<ScopedMessageHandler> logger) : IRawMessageHandler
{
    public async Task<ProcessingOutcome> HandleAsync(RawEnvelope envelope, CancellationToken cancellationToken)
    {
        var message = RawMessageDecoder.Decode(envelope);

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(message.TenantId, null, TenantPrincipalKind.None);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = message.TenantId });

        var outcome = await scope.ServiceProvider.GetRequiredService<ProfilePipeline>().HandleAsync(message, cancellationToken).ConfigureAwait(false);

        if (outcome.Redirected)
        {
            logger.LogInformation(
                "MergedIntoId redirect: {Topic}[{Partition}]@{Offset} resolved to a merge loser and was applied to winner {ProfileId}",
                envelope.Topic, envelope.Partition, envelope.Offset, outcome.ProfileId);
        }

        return outcome;
    }
}
```

`Consumers/SchemaGate.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crm.Profiles.Infrastructure.Consumers;

/// <summary>
/// Ruling E11: in compose the API applies migrations and the Worker may start first. Consuming before profiles.Events
/// exists would dead-letter valid records, so the consumer waits here instead.
/// </summary>
internal sealed class SchemaGate(IServiceScopeFactory scopes, ILogger<SchemaGate> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        var announced = false;
        while (true)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ProfilesDbContext>();
                var present = await db.Database
                    .SqlQuery<int>($"SELECT CASE WHEN OBJECT_ID(N'profiles.Events') IS NULL THEN 0 ELSE 1 END AS [Value]")
                    .SingleAsync(cancellationToken).ConfigureAwait(false);
                if (present == 1)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Schema probe failed; the database may not be up yet");
            }

            if (!announced)
            {
                logger.LogInformation("Waiting for schema 'profiles' (applied by the API in Development, or by `make migrate`)");
                announced = true;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

`Consumers/RawEventsConsumerService.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Application.Retry;
using Crm.Profiles.Infrastructure.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Profiles.Infrastructure.Consumers;

/// <summary>
/// events.raw → profiles. Order per record (spec §4.2 step 5, NFR-04): SQL commit → publish to events.processed →
/// commit the offset. A record that fails every attempt goes to events.dlq and its offset is committed (FR-07).
/// Anything that fails outside message processing tears the consumer down; it is rebuilt after a pause and Kafka
/// redelivers from the last committed offset (ruling E9).
/// </summary>
internal sealed class RawEventsConsumerService(
    IOptions<ProfilesConsumerOptions> options,
    IRawMessageHandler handler,
    IProcessedPublisher processed,
    IDeadLetterPublisher deadLetters,
    IPipelineHooks hooks,
    SchemaGate schemaGate,
    ProfilesMetrics metrics,
    ILogger<RawEventsConsumerService> logger) : BackgroundService
{
    private static readonly ActivitySource Source = new("Crm.Profiles");
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LagInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never delay host start-up (see StartupProbeService)

        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("events.raw consumer is disabled (Profiles:Consumer:Enabled=false)");
            return;
        }

        try
        {
            await schemaGate.WaitAsync(stoppingToken).ConfigureAwait(false);
            var instances = Enumerable.Range(0, Math.Max(1, settings.Instances))
                .Select(instance => Task.Run(() => RunInstanceAsync(instance, settings, stoppingToken), stoppingToken));
            await Task.WhenAll(instances).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task RunInstanceAsync(int instance, ProfilesConsumerOptions settings, CancellationToken stoppingToken)
    {
        var retry = new MessageRetryPolicy(settings.RetryDelays);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var consumer = BuildConsumer(instance, settings);
                consumer.Subscribe(KafkaTopics.EventsRaw);
                try
                {
                    await ConsumeLoopAsync(consumer, retry, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    CloseQuietly(consumer);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "events.raw consumer {Instance} stopped outside message processing; rebuilding in {DelayMs} ms, uncommitted records will be redelivered", instance, settings.RestartDelayMs);
                try
                {
                    await Task.Delay(settings.RestartDelayMs, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ConsumeLoopAsync(IConsumer<string, byte[]> consumer, MessageRetryPolicy retry, CancellationToken stoppingToken)
    {
        var sinceLagReport = Stopwatch.StartNew();
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = consumer.Consume(PollInterval);
            if (sinceLagReport.Elapsed >= LagInterval)
            {
                ReportLag(consumer);
                sinceLagReport.Restart();
            }

            if (result?.Message is null)
            {
                continue;
            }

            await ProcessAsync(result, retry, stoppingToken).ConfigureAwait(false);
            consumer.Commit(result);
        }

        stoppingToken.ThrowIfCancellationRequested();
    }

    private async Task ProcessAsync(ConsumeResult<string, byte[]> result, MessageRetryPolicy retry, CancellationToken stoppingToken)
    {
        var envelope = ToEnvelope(result);
        using var activity = StartActivity(envelope);

        var attempt = await retry.ExecuteAsync((_, token) => handler.HandleAsync(envelope, token), stoppingToken).ConfigureAwait(false);
        if (!attempt.Succeeded)
        {
            activity?.SetStatus(ActivityStatusCode.Error, attempt.Failure!.Message);
            logger.LogError(attempt.Failure, "Dead-lettering {Topic}[{Partition}]@{Offset} after {Attempts} attempts", envelope.Topic, envelope.Partition, envelope.Offset, attempt.Attempts);
            await deadLetters.PublishAsync(envelope, attempt.Failure!, attempt.Attempts, stoppingToken).ConfigureAwait(false);
            metrics.DeadLettered(envelope.Topic);
            return;
        }

        await hooks.SqlCommittedAsync(envelope, stoppingToken).ConfigureAwait(false);
        foreach (var message in attempt.Value!.Messages)
        {
            await processed.PublishAsync(message, stoppingToken).ConfigureAwait(false);
        }
    }

    private IConsumer<string, byte[]> BuildConsumer(int instance, ProfilesConsumerOptions settings)
        => new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = settings.BootstrapServers,
                GroupId = settings.GroupId,
                ClientId = $"crm-worker-profiles-{instance}",
                EnableAutoCommit = false,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnablePartitionEof = false,
            })
            .SetPartitionsRevokedHandler((_, partitions) => ClearLag(partitions.Select(p => p.TopicPartition)))
            .SetPartitionsLostHandler((_, partitions) => ClearLag(partitions.Select(p => p.TopicPartition)))
            .Build();

    private void ReportLag(IConsumer<string, byte[]> consumer)
    {
        foreach (var partition in consumer.Assignment)
        {
            var high = consumer.GetWatermarkOffsets(partition).High; // cached from the last fetch; no network call
            var position = consumer.Position(partition);
            if (high == Offset.Unset || position == Offset.Unset)
            {
                continue; // nothing fetched from this partition yet
            }

            metrics.SetLag(partition.Topic, partition.Partition.Value, Math.Max(0, high.Value - position.Value));
        }
    }

    private void ClearLag(IEnumerable<TopicPartition> partitions)
    {
        foreach (var partition in partitions)
        {
            metrics.ClearLag(partition.Topic, partition.Partition.Value);
        }
    }

    private void CloseQuietly(IConsumer<string, byte[]> consumer)
    {
        try
        {
            consumer.Close(); // leave the group now instead of after the session timeout
        }
        catch (KafkaException ex)
        {
            logger.LogDebug(ex, "Closing the events.raw consumer failed");
        }
    }

    private static RawEnvelope ToEnvelope(ConsumeResult<string, byte[]> result)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in result.Message.Headers)
        {
            headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes() ?? []);
        }

        return new RawEnvelope(result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, result.Message.Value ?? [], headers);
    }

    private static Activity? StartActivity(RawEnvelope envelope)
    {
        var name = $"{envelope.Topic} process";
        var activity = envelope.Headers.TryGetValue(KafkaHeaders.TraceParent, out var traceParent) && ActivityContext.TryParse(traceParent, null, out var parent)
            ? Source.StartActivity(name, ActivityKind.Consumer, parent)
            : Source.StartActivity(name, ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", envelope.Topic);
        activity?.SetTag("messaging.kafka.partition", envelope.Partition);
        activity?.SetTag("messaging.kafka.offset", envelope.Offset);
        return activity;
    }
}
```

`ProfilesWorkerRegistration.cs`:

```csharp
using Crm.Profiles.Application.Pipeline;
using Crm.Profiles.Infrastructure.Consumers;
using Crm.Profiles.Infrastructure.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Profiles.Infrastructure;

public static class ProfilesWorkerRegistration
{
    /// <summary>The Worker's half of the module: everything that consumes or produces Kafka. The API never calls this.</summary>
    public static IServiceCollection AddProfilesWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ProfilesConsumerOptions>()
            .Bind(configuration.GetSection(ProfilesConsumerOptions.SectionName))
            .Configure(o => o.BootstrapServers = configuration["Kafka:BootstrapServers"] ?? string.Empty)
            .Validate(o => !string.IsNullOrWhiteSpace(o.BootstrapServers), "Kafka:BootstrapServers is required.")
            .Validate(o => o.Instances is >= 1 and <= 64, "Profiles:Consumer:Instances must be between 1 and 64.")
            .Validate(o => o.RetryDelaysMs.All(ms => ms >= 0) && o.RestartDelayMs >= 0, "Profiles:Consumer delays must not be negative.")
            .ValidateOnStart();

        services.AddSingleton<ProfilesMetrics>();
        services.AddSingleton<SchemaGate>();
        services.AddSingleton<WorkerProducer>();
        services.AddSingleton<IProcessedPublisher, ProcessedEventPublisher>();
        services.AddSingleton<IDeadLetterPublisher, DeadLetterPublisher>();
        services.AddSingleton<IRawMessageHandler, ScopedMessageHandler>();
        services.AddHostedService<RawEventsConsumerService>();
        return services;
    }
}
```

`src/Hosts/Crm.Worker/Program.cs` — change the module registration to:

```csharp
        builder.Services
            .AddProfilesModule(builder.Configuration)
            .AddProfilesWorker(builder.Configuration)
            .AddSegmentationModule(builder.Configuration);
```

`StartupProbeService.cs` — replace the class doc comment with `/// <summary>Logs Kafka broker metadata once at start-up, so a wrong bootstrap address is visible in the first log lines.</summary>`.

`src/Hosts/Crm.Worker/appsettings.json` — add after the `"Kafka"` block:

```json
  "Profiles": {
    "Consumer": {
      "Enabled": true,
      "Instances": 4
    }
  },
```

Run: `dotnet build Crm.sln -warnaserror` → 0 warnings; `dotnet test Crm.sln --filter "Category!=Integration"` → green (AT-005: the Worker still references Infrastructure projects only).

- [ ] **Step 5: Shared test Worker and helpers**

`tests/Crm.IntegrationTests/Fixtures/SwitchableHooks.cs`:

```csharp
using Crm.Profiles.Application.Messages;
using Crm.Profiles.Application.Pipeline;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// The shared Worker's pipeline hooks. Inert until a test installs a callback; callbacks receive the tenant or the
/// record, so a test reacts to its own data only. Dispose the returned handle to uninstall.
/// </summary>
public sealed class SwitchableHooks : IPipelineHooks
{
    private Func<Guid, CancellationToken, Task>? _mergeLocksTaken;
    private Func<RawEnvelope, CancellationToken, Task>? _sqlCommitted;

    public Task MergeLocksTakenAsync(Guid tenantId, CancellationToken cancellationToken)
        => _mergeLocksTaken?.Invoke(tenantId, cancellationToken) ?? Task.CompletedTask;

    public Task SqlCommittedAsync(RawEnvelope envelope, CancellationToken cancellationToken)
        => _sqlCommitted?.Invoke(envelope, cancellationToken) ?? Task.CompletedTask;

    public IDisposable OnMergeLocksTaken(Func<Guid, CancellationToken, Task> callback)
    {
        _mergeLocksTaken = callback;
        return new Uninstall(() => _mergeLocksTaken = null);
    }

    public IDisposable OnSqlCommitted(Func<RawEnvelope, CancellationToken, Task> callback)
    {
        _sqlCommitted = callback;
        return new Uninstall(() => _sqlCommitted = null);
    }

    private sealed class Uninstall(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
```

`tests/Crm.IntegrationTests/Fixtures/CrmWorkerFactory.cs`:

```csharp
using Crm.Profiles.Application.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// Hosts Crm.Worker in-process (docs/test/integration.md: "the Worker as a hosted service in the same test host").
/// One instance serves the whole run (ruling E16): it consumes group `profiles` like production, with short retry
/// and restart delays, and with <see cref="SwitchableHooks"/> in place of the no-op hooks.
/// </summary>
public sealed class CrmWorkerFactory : WebApplicationFactory<Crm.Worker.Program>
{
    private readonly CrmStackFixture _stack;
    private readonly SwitchableHooks _hooks;

    public CrmWorkerFactory(CrmStackFixture stack, SwitchableHooks hooks)
    {
        _stack = stack;
        _hooks = hooks;

        // Crm.Worker.Program reads connection settings while building, before ConfigureAppConfiguration applies (see CrmApiFactory).
        Environment.SetEnvironmentVariable("ConnectionStrings__Crm", stack.SqlConnectionString);
        Environment.SetEnvironmentVariable("Redis__Configuration", stack.RedisConfiguration);
        Environment.SetEnvironmentVariable("Kafka__BootstrapServers", stack.KafkaBootstrapServers);
    }

    /// <summary>Builds and starts the host (hosted services included).</summary>
    public void Start() => _ = Services;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Crm"] = _stack.SqlConnectionString,
            ["Redis:Configuration"] = _stack.RedisConfiguration,
            ["Kafka:BootstrapServers"] = _stack.KafkaBootstrapServers,
            ["Profiles:Consumer:RetryDelaysMs:0"] = "50",
            ["Profiles:Consumer:RetryDelaysMs:1"] = "100",
            ["Profiles:Consumer:RestartDelayMs"] = "200",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPipelineHooks>();
            services.AddSingleton<IPipelineHooks>(_hooks);
        });
    }
}
```

`CrmStackFixture.cs` — add properties and lifecycle:

```csharp
    public SwitchableHooks Hooks { get; } = new();

    public CrmWorkerFactory Worker { get; private set; } = null!;
```

at the end of `InitializeAsync`:

```csharp
        Worker = new CrmWorkerFactory(this, Hooks);
        Worker.Start();
```

and make `DisposeAsync` stop the Worker before the containers go away:

```csharp
    public async Task DisposeAsync()
    {
        if (Worker is not null)
        {
            await Worker.DisposeAsync();
        }

        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _kafka.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
```

`tests/Crm.IntegrationTests/Fixtures/Eventually.cs`:

```csharp
using System.Diagnostics;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>The pipeline is asynchronous; assertions poll. The remote engine adds network round trips, so its budget is wider.</summary>
public static class Eventually
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    public static TimeSpan DefaultTimeout
        => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE")) ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(60);

    public static async Task<T> GetAsync<T>(Func<Task<T?>> probe, string because, TimeSpan? timeout = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(probe);
        var budget = timeout ?? DefaultTimeout;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            if (clock.Elapsed > budget)
            {
                throw new TimeoutException($"Not within {budget.TotalSeconds:0} s: {because}");
            }

            await Task.Delay(Poll);
        }
    }

    public static async Task TrueAsync(Func<Task<bool>> probe, string because, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        await GetAsync<object>(async () => await probe() ? new object() : null, because, timeout);
    }
}
```

`tests/Crm.IntegrationTests/Fixtures/ProfilesDb.cs`:

```csharp
using Microsoft.Data.SqlClient;

namespace Crm.IntegrationTests.Fixtures;

public sealed record ProfileRow(Guid Id, string? ExternalId, string? Email, string? Phone, string Attributes, DateTime FirstSeenAt, DateTime LastSeenAt, long EventCount, Guid? MergedIntoId);

public sealed record EventRow(Guid Id, Guid ProfileId, string Name, DateTime OccurredAt);

/// <summary>Reads schema `profiles` with plain SQL: plan E has no read API, and assertions should not share code with what they check.</summary>
public static class ProfilesDb
{
    private const string ProfileColumns = "p.Id, p.ExternalId, p.Email, p.Phone, p.Attributes, p.FirstSeenAt, p.LastSeenAt, p.EventCount, p.MergedIntoId";

    public static Task<ProfileRow?> FindByIdentityAsync(string connectionString, Guid tenantId, string kind, string value)
        => SingleProfileAsync(
            connectionString,
            $"SELECT {ProfileColumns} FROM profiles.UserIdentities i JOIN profiles.Profiles p ON p.Id = i.ProfileId WHERE i.TenantId = @tenant AND i.Kind = @kind AND i.Value = @value",
            new SqlParameter("@tenant", tenantId), new SqlParameter("@kind", kind), new SqlParameter("@value", value));

    public static Task<ProfileRow?> GetAsync(string connectionString, Guid tenantId, Guid profileId)
        => SingleProfileAsync(
            connectionString,
            $"SELECT {ProfileColumns} FROM profiles.Profiles p WHERE p.TenantId = @tenant AND p.Id = @id",
            new SqlParameter("@tenant", tenantId), new SqlParameter("@id", profileId));

    public static async Task<int> CountProfilesAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM profiles.Profiles WHERE TenantId = @tenant";
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        return (int)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<IReadOnlyList<EventRow>> EventsAsync(string connectionString, Guid tenantId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ProfileId, Name, OccurredAt FROM profiles.Events WHERE TenantId = @tenant ORDER BY OccurredAt, Id";
        command.Parameters.Add(new SqlParameter("@tenant", tenantId));
        var rows = new List<EventRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new EventRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetDateTime(3)));
        }

        return rows;
    }

    private static async Task<ProfileRow?> SingleProfileAsync(string connectionString, string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ProfileRow(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetDateTime(5),
            reader.GetDateTime(6),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8));
    }
}
```

(If an analyzer flags the interpolated `ProfileColumns` constant as possible SQL injection (CA2100), inline the column list into both literals.)

`tests/Crm.IntegrationTests/Fixtures/TopicReader.cs`:

```csharp
using System.Text;
using Confluent.Kafka;
using Crm.Contracts;

namespace Crm.IntegrationTests.Fixtures;

public sealed record TopicRecord(string Topic, int Partition, long Offset, string? Key, byte[] Value, IReadOnlyDictionary<string, string> Headers)
{
    public string MessageType => Headers.GetValueOrDefault(KafkaHeaders.MessageType, string.Empty);
}

/// <summary>Like RawTopicReader, for any topic and without parsing the value (a dead letter need not be JSON).</summary>
public static class TopicReader
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    public static Task<IReadOnlyList<TopicRecord>> ReadTenantAsync(string bootstrapServers, string topic, Guid tenantId, int expected, TimeSpan timeout)
        => Task.Run<IReadOnlyList<TopicRecord>>(() =>
        {
            using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = $"it-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            }).Build();
            consumer.Subscribe(topic);

            var tenant = tenantId.ToString("N");
            var records = new List<TopicRecord>();
            var deadline = DateTime.UtcNow + timeout;
            DateTime? quietUntil = null;
            while (DateTime.UtcNow < deadline && (quietUntil is null || DateTime.UtcNow < quietUntil))
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (result?.Message is null)
                {
                    continue;
                }

                var headers = result.Message.Headers.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes() ?? []), StringComparer.Ordinal);
                if (!headers.TryGetValue(KafkaHeaders.TenantId, out var owner) || owner != tenant)
                {
                    continue;
                }

                records.Add(new TopicRecord(result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, result.Message.Value ?? [], headers));
                if (records.Count >= expected)
                {
                    quietUntil = DateTime.UtcNow + Quiet;
                }
            }

            consumer.Close();
            return records;
        });
}
```

- [ ] **Step 6: Write the pipeline smoke test**

`tests/Crm.IntegrationTests/Ingestion/TrackPipelineTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

/// <summary>API → events.raw → Worker → schema profiles → events.processed. IT-001's timeline half needs the read API (plan F).</summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class TrackPipelineTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_tracked_event_reaches_the_profile_timeline_and_is_announced_on_events_processed()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase", properties = new { amount = 49.9 } });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();

        var row = await Eventually.GetAsync(
            async () => (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).SingleOrDefault(e => e.Id == eventId),
            "the Worker appends the event to profiles.Events");
        row.Name.Should().Be("purchase");

        var profile = await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u1");
        profile.Should().NotBeNull();
        profile!.Id.Should().Be(row.ProfileId);
        profile.ExternalId.Should().Be("u1");
        profile.EventCount.Should().Be(1);
        profile.MergedIntoId.Should().BeNull();

        var processed = await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsProcessed, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(30));
        processed.Should().NotBeEmpty("events.processed is at-least-once: a rebalance while the Worker starts can announce the event twice (ruling E7)");
        var record = processed[0];
        record.MessageType.Should().Be(nameof(EventTracked));
        record.Key.Should().Be($"{tenant.TenantId:N}:{profile.Id:N}");
        record.Headers[KafkaHeaders.ContentType].Should().Be(KafkaHeaders.JsonContentType);
        record.Headers.Should().ContainKey(KafkaHeaders.TraceParent);
        var payload = JsonSerializer.Deserialize<EventTracked>(record.Value, ContractJson.Options)!;
        payload.EventId.Should().Be(eventId);
        payload.ProfileId.Should().Be(profile.Id);
        payload.Profile.ExternalId.Should().Be("u1");
    }

    [Fact]
    public async Task The_worker_exports_consumer_lag()
    {
        using var worker = stack.Worker.CreateClient();

        await Eventually.TrueAsync(
            async () => (await worker.GetStringAsync(new Uri("/metrics", UriKind.Relative))).Contains("crm_consumer_lag{", StringComparison.Ordinal),
            "lag is reported every 5 s for partitions that have fetched at least once",
            TimeSpan.FromSeconds(90));
    }
}
```

`PostWithKeyAsync` is `Crm.IntegrationTests.Ingestion.IngestClient` (same namespace). Lag is only reported for partitions that fetched a record, so run the class as a whole (the first test produces one).

- [ ] **Step 7: Run and verify**

With the environment prefix: `dotnet test Crm.sln --filter "Category=Integration&FullyQualifiedName~TrackPipelineTests"` → 2 passed. Then the **full** integration suite once (the shared Worker now runs during every class; nothing else may break) → all green, 0 skipped.

If the second host in the process makes Serilog throw "The logger is already frozen", do not swallow it: report it — `AddCrmSerilog` then needs the same treatment for two hosts that it already has for consecutive API factories.

- [ ] **Step 8: Commit**

```bash
git add src/Modules/Profiles/Profiles.Infrastructure src/Hosts/Crm.Worker tests/Profiles.UnitTests tests/Crm.IntegrationTests
git commit -m "feat(worker): events.raw consumer — retry then DLQ, publish after SQL commit, lag and DLQ metrics, schema gate

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Pipeline integration cases — IT-002, IT-004, IT-005, IT-006

**Files:**
- Create: `tests/Crm.IntegrationTests/Fixtures/KafkaTestClient.cs`, `tests/Crm.IntegrationTests/Fixtures/WorkerMetrics.cs`
- Create: `tests/Crm.IntegrationTests/Ingestion/IdentifyTests.cs`, `IdempotencyTests.cs`, `DeadLetterTests.cs`, `CommitOrderingTests.cs`
- Modify (only if a test exposes a defect): files of Tasks 3–5

**Interfaces:**
- Consumes: `CrmStackFixture.Worker`, `.Hooks`, `Eventually`, `ProfilesDb`, `TopicReader`, `TopicRecord` (Task 5); `TenancySeed`, `CrmApiFactory`, `IngestClient.PostWithKeyAsync` (existing); `KafkaTopics.ProfilesConsumerGroup`.
- Produces: `KafkaTestClient.ProduceAsync(string bootstrapServers, string topic, string? key, byte[] value, IReadOnlyDictionary<string, string> headers) : Task<TopicPartitionOffset>`, `KafkaTestClient.CommittedOffset(string bootstrapServers, string groupId, TopicPartition partition) : long`, `WorkerMetrics.DeadLetteredAsync(HttpClient worker, string topic) : Task<double>` — Task 7 reuses the first.

These are tests of finished behaviour: each is expected to pass once written. Where one fails, find the cause in Tasks 3–5 code (use superpowers:systematic-debugging), fix it there with a unit test when the defect is reproducible without containers, and describe it in your report.

- [ ] **Step 1: Helpers**

`tests/Crm.IntegrationTests/Fixtures/KafkaTestClient.cs`:

```csharp
using System.Text;
using Confluent.Kafka;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>Raw access to the broker for tests that must produce a record the API would never produce, or look at a group's offsets.</summary>
public static class KafkaTestClient
{
    public static async Task<TopicPartitionOffset> ProduceAsync(string bootstrapServers, string topic, string? key, byte[] value, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        using var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = bootstrapServers, Acks = Acks.All }).Build();
        var kafkaHeaders = new Headers();
        foreach (var (name, text) in headers)
        {
            kafkaHeaders.Add(name, Encoding.UTF8.GetBytes(text));
        }

        var report = await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key!, Value = value, Headers = kafkaHeaders });
        return report.TopicPartitionOffset;
    }

    /// <summary>The next offset the group will read on the partition; -1001 (Offset.Unset) when it never committed there. Does not join the group.</summary>
    public static long CommittedOffset(string bootstrapServers, string groupId, TopicPartition partition)
    {
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig { BootstrapServers = bootstrapServers, GroupId = groupId, EnableAutoCommit = false }).Build();
        return consumer.Committed([partition], TimeSpan.FromSeconds(10)).Single().Offset.Value;
    }
}
```

`tests/Crm.IntegrationTests/Fixtures/WorkerMetrics.cs`:

```csharp
using System.Globalization;

namespace Crm.IntegrationTests.Fixtures;

public static class WorkerMetrics
{
    /// <summary>Current value of crm_dlq_total{topic="…"} on the Worker's /metrics; 0 while the series does not exist yet.</summary>
    public static async Task<double> DeadLetteredAsync(HttpClient worker, string topic)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var body = await worker.GetStringAsync(new Uri("/metrics", UriKind.Relative));
        var line = body.Split('\n').FirstOrDefault(l =>
            l.StartsWith("crm_dlq_total{", StringComparison.Ordinal) && l.Contains($"topic=\"{topic}\"", StringComparison.Ordinal));
        if (line is null)
        {
            return 0;
        }

        // `name{labels} value [timestamp]` — the value is the first token after the closing brace.
        var afterLabels = line[(line.LastIndexOf('}') + 1)..].Trim();
        return double.Parse(afterLabels.Split(' ')[0], CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 2: IT-002**

`tests/Crm.IntegrationTests/Ingestion/IdentifyTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class IdentifyTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT002_identify_creates_a_profile_and_applies_traits()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var first = await _client.PostWithKeyAsync("/v1/identify", key.Plaintext, new { userId = "u2", traits = new { plan = "pro" } });
        first.StatusCode.Should().Be(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync());
        var afterFirst = await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u2"),
            "the first identify creates the profile");
        Plan(afterFirst).Should().Be("pro");

        await Task.Delay(TimeSpan.FromMilliseconds(20)); // datetime2(3): make sure the second ReceivedAt is a later millisecond
        using var second = await _client.PostWithKeyAsync("/v1/identify", key.Plaintext, new { userId = "u2", traits = new { plan = "free", city = "SG" } });
        second.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var profile = await Eventually.GetAsync(
            async () => await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u2") is { } p && Plan(p) == "free" ? p : null,
            "the second identify overwrites plan (same Kafka key, so the two are applied in order)");

        using var attributes = JsonDocument.Parse(profile.Attributes);
        attributes.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("plan", "city");
        attributes.RootElement.GetProperty("city").GetString().Should().Be("SG");
        profile.EventCount.Should().Be(0);
        profile.LastSeenAt.Should().BeAfter(afterFirst.LastSeenAt);
        profile.FirstSeenAt.Should().Be(afterFirst.FirstSeenAt);
        (await ProfilesDb.CountProfilesAsync(stack.SqlConnectionString, tenant.TenantId)).Should().Be(1);
        (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).Should().BeEmpty("identify never writes a timeline row (FR-10)");
    }

    private static string? Plan(ProfileRow row)
    {
        using var document = JsonDocument.Parse(row.Attributes);
        return document.RootElement.TryGetProperty("plan", out var plan) ? plan.GetString() : null;
    }
}
```

- [ ] **Step 3: IT-004**

`tests/Crm.IntegrationTests/Ingestion/IdempotencyTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class IdempotencyTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT004_duplicate_event_id_is_ignored()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase" });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();
        await Eventually.TrueAsync(
            async () => (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).Any(e => e.Id == eventId),
            "the first delivery is processed");

        // The identical record again: same key (same partition), same bytes, same headers — what a Kafka redelivery looks like.
        var original = (await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsRaw, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(30))).Single();
        var headers = original.Headers.Where(h => h.Key != KafkaHeaders.TraceParent).ToDictionary(h => h.Key, h => h.Value);
        var duplicate = await KafkaTestClient.ProduceAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsRaw, original.Key, original.Value, headers);
        duplicate.Partition.Should().Be(new Partition(original.Partition));

        await Eventually.TrueAsync(
            () => Task.FromResult(KafkaTestClient.CommittedOffset(stack.KafkaBootstrapServers, KafkaTopics.ProfilesConsumerGroup, duplicate.TopicPartition) > duplicate.Offset.Value),
            "the consumer offset advances past the duplicate");

        (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).Should().ContainSingle(e => e.Id == eventId);
        var profile = await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u1");
        profile!.EventCount.Should().Be(1);

        var announced = await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsProcessed, tenant.TenantId, expected: 2, TimeSpan.FromSeconds(30));
        announced.Count(r => r.MessageType == nameof(EventTracked)).Should().BeGreaterThanOrEqualTo(2, "a duplicate is announced again (ruling E7): events.processed is at-least-once");
    }
}
```

- [ ] **Step 4: IT-005**

`tests/Crm.IntegrationTests/Ingestion/DeadLetterTests.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Text;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class DeadLetterTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT005_poison_message_goes_to_the_dlq_after_three_attempts_and_the_next_event_is_processed()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        using var worker = stack.Worker.CreateClient();
        var deadLetteredBefore = await WorkerMetrics.DeadLetteredAsync(worker, KafkaTopics.EventsRaw);

        var poison = Encoding.UTF8.GetBytes($$"""{"eventId":"{{Guid.CreateVersion7()}}","tenantId":"{{tenant.TenantId}}","userId":"u-poison","event":"purchase","properties":{"a":""");
        var produced = await KafkaTestClient.ProduceAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsRaw, $"{tenant.TenantId:N}:u-poison", poison, new Dictionary<string, string>
        {
            [KafkaHeaders.TenantId] = tenant.TenantId.ToString("N"),
            [KafkaHeaders.MessageType] = nameof(TrackEventRaw),
            [KafkaHeaders.ContentType] = KafkaHeaders.JsonContentType,
        });

        using var valid = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u-valid", @event = "purchase" });
        valid.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var letters = await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsDlq, tenant.TenantId, expected: 1, Eventually.DefaultTimeout);
        var letter = letters.Should().ContainSingle().Subject;
        letter.Value.Should().Equal(poison, "the DLQ carries the original bytes");
        letter.Key.Should().Be($"{tenant.TenantId:N}:u-poison");
        letter.Headers[KafkaHeaders.DlqError].Should().StartWith("PoisonMessageException");
        letter.Headers[KafkaHeaders.DlqStack].Should().NotBeNullOrWhiteSpace();
        letter.Headers[KafkaHeaders.DlqAttempt].Should().Be("3");
        letter.Headers[KafkaHeaders.DlqSourceTopic].Should().Be(KafkaTopics.EventsRaw);
        letter.Headers[KafkaHeaders.DlqSourcePartition].Should().Be(produced.Partition.Value.ToString(CultureInfo.InvariantCulture));
        letter.Headers[KafkaHeaders.DlqSourceOffset].Should().Be(produced.Offset.Value.ToString(CultureInfo.InvariantCulture));
        letter.Headers[KafkaHeaders.MessageType].Should().Be(nameof(TrackEventRaw), "original headers survive");

        await Eventually.TrueAsync(
            async () => await WorkerMetrics.DeadLetteredAsync(worker, KafkaTopics.EventsRaw) >= deadLetteredBefore + 1,
            "crm_dlq_total{topic=\"events.raw\"} increments");
        (await WorkerMetrics.DeadLetteredAsync(worker, KafkaTopics.EventsRaw)).Should().Be(deadLetteredBefore + 1);

        await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u-valid"),
            "the Worker keeps going after a poison message (FR-07)");
        (await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u-poison")).Should().BeNull();
        KafkaTestClient.CommittedOffset(stack.KafkaBootstrapServers, KafkaTopics.ProfilesConsumerGroup, produced.TopicPartition)
            .Should().BeGreaterThan(produced.Offset.Value, "a dead-lettered record is committed, not retried forever");
    }
}
```

- [ ] **Step 5: IT-006**

`tests/Crm.IntegrationTests/Ingestion/CommitOrderingTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Confluent.Kafka;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class CommitOrderingTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT006_offset_is_committed_only_after_the_sql_transaction()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        var recordKey = $"{tenant.TenantId:N}:u-crash";
        var deliveries = 0;

        // The fault: SQL has committed, then the consumer dies before it publishes or commits the offset.
        using var fault = stack.Hooks.OnSqlCommitted((envelope, _) =>
        {
            if (envelope.Key == recordKey && Interlocked.Increment(ref deliveries) == 1)
            {
                throw new InvalidOperationException("IT-006: simulated crash after the SQL commit");
            }

            return Task.CompletedTask;
        });

        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u-crash", @event = "purchase" });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();

        await Eventually.TrueAsync(() => Task.FromResult(Volatile.Read(ref deliveries) >= 2), "the record is redelivered because its offset was never committed");

        var raw = (await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsRaw, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(30))).Single();
        var partition = new TopicPartition(KafkaTopics.EventsRaw, new Partition(raw.Partition));
        await Eventually.TrueAsync(
            () => Task.FromResult(KafkaTestClient.CommittedOffset(stack.KafkaBootstrapServers, KafkaTopics.ProfilesConsumerGroup, partition) > raw.Offset),
            "the second delivery commits the offset");

        (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).Should().ContainSingle(e => e.Id == eventId, "the first delivery committed SQL; the second is a duplicate (IT-004)");
        (await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u-crash"))!.EventCount.Should().Be(1);

        var announced = await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsProcessed, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(30));
        announced.Should().Contain(r => r.MessageType == nameof(EventTracked), "the crash happened before the publish, so the redelivery must announce the event (ruling E7)");
    }
}
```

- [ ] **Step 6: Run**

With the environment prefix: `dotnet test Crm.sln --filter "Category=Integration&(FullyQualifiedName~IdentifyTests|FullyQualifiedName~IdempotencyTests|FullyQualifiedName~DeadLetterTests|FullyQualifiedName~CommitOrderingTests)"` → 4 passed. Then the full integration suite once → green, 0 skipped. `dotnet build Crm.sln -warnaserror` → 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add tests/Crm.IntegrationTests src tests/Profiles.UnitTests
git commit -m "test(integration): identify, idempotent redelivery, dead letters and commit ordering through the real pipeline (IT-002, IT-004..IT-006)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Merge integration cases — IT-007, IT-008, IT-009, IT-010

**Files:**
- Create: `tests/Crm.IntegrationTests/Fixtures/RawEnvelopes.cs`, `tests/Crm.IntegrationTests/Profiles/MergeTests.cs`, `tests/Crm.IntegrationTests/Profiles/MergeConcurrencyTests.cs`
- Modify (only if a test exposes a defect): files of Tasks 3–5

**Interfaces:**
- Consumes: everything Task 6 consumes; `IRawMessageHandler` from `stack.Worker.Services`; `RawEnvelope`; `RedisKeys.IdentityCache`; `StackExchange.Redis` (already flows into the test project through the hosts).
- Produces: `RawEnvelopes.Track(Guid tenantId, string? userId, string? anonymousId, DateTime at)`, `RawEnvelopes.Identify(Guid tenantId, string userId, string? anonymousId, string traitsJson, DateTime at)` → `RawEnvelope`.

Same rule as Task 6: these tests describe finished behaviour; a failure is a defect to find and fix at its source. The likeliest one is the unique-index hand-over inside one `SaveChanges` — see the note in Task 4 Step 3.

- [ ] **Step 1: Envelope builder**

`tests/Crm.IntegrationTests/Fixtures/RawEnvelopes.cs`:

```csharp
using System.Text.Json;
using Crm.Contracts;
using Crm.Profiles.Application.Messages;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>Records as the API would have produced them, for tests that call the Worker's handler without going through Kafka.</summary>
public static class RawEnvelopes
{
    public static RawEnvelope Track(Guid tenantId, string? userId, string? anonymousId, DateTime at)
        => Build(tenantId, userId ?? anonymousId!, nameof(TrackEventRaw), new TrackEventRaw(Guid.CreateVersion7(), tenantId, userId, anonymousId, "purchase", Json("{}"), at, at));

    public static RawEnvelope Identify(Guid tenantId, string userId, string? anonymousId, string traitsJson, DateTime at)
        => Build(tenantId, userId, nameof(IdentifyRaw), new IdentifyRaw(Guid.CreateVersion7(), tenantId, userId, anonymousId, Json(traitsJson), at));

    private static RawEnvelope Build<T>(Guid tenantId, string userKey, string messageType, T payload)
        => new(KafkaTopics.EventsRaw, 0, 0, $"{tenantId:N}:{userKey}", JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options), new Dictionary<string, string>
        {
            [KafkaHeaders.TenantId] = tenantId.ToString("N"),
            [KafkaHeaders.MessageType] = messageType,
            [KafkaHeaders.ContentType] = KafkaHeaders.JsonContentType,
        });

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
```

- [ ] **Step 2: IT-007, IT-009, IT-010**

`tests/Crm.IntegrationTests/Profiles/MergeTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Crm.Contracts;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using Crm.Kernel.Redis;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class MergeTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task SendAsync(string path, string apiKey, object body)
    {
        using var response = await _client.PostWithKeyAsync(path, apiKey, body);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
    }

    private Task<ProfileRow> ProfileWithEventsAsync(Guid tenantId, string kind, string value, long events)
        => Eventually.GetAsync(
            async () => await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenantId, kind, value) is { } p && p.EventCount == events ? p : null,
            $"{kind}:{value} has {events} event(s)");

    [Fact]
    public async Task IT007_identify_merges_anonymous_into_known_profile()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = (await TenancySeed.AddApiKeyAsync(_factory, tenant)).Plaintext;

        await SendAsync("/v1/track", key, new { anonymousId = "a1", @event = "view" });
        await SendAsync("/v1/track", key, new { anonymousId = "a1", @event = "view" });
        var p1 = await ProfileWithEventsAsync(tenant.TenantId, "anonymous_id", "a1", 2);
        await SendAsync("/v1/track", key, new { userId = "u1", @event = "purchase" });
        var p2 = await ProfileWithEventsAsync(tenant.TenantId, "external_id", "u1", 1);
        p2.FirstSeenAt.Should().BeAfter(p1.FirstSeenAt, "the test needs P1 to be the older profile");

        await SendAsync("/v1/identify", key, new { userId = "u1", anonymousId = "a1", traits = new { plan = "pro" } });

        var loser = await Eventually.GetAsync(
            async () => await ProfilesDb.GetAsync(stack.SqlConnectionString, tenant.TenantId, p2.Id) is { MergedIntoId: not null } merged ? merged : null,
            "P2 is marked as merged");
        loser.MergedIntoId.Should().Be(p1.Id, "the older profile wins (FR-09)");
        loser.ExternalId.Should().BeNull("a unique value has one owner");

        var winner = (await ProfilesDb.GetAsync(stack.SqlConnectionString, tenant.TenantId, p1.Id))!;
        winner.MergedIntoId.Should().BeNull();
        winner.ExternalId.Should().Be("u1");
        winner.EventCount.Should().Be(3);
        using (var attributes = JsonDocument.Parse(winner.Attributes))
        {
            attributes.RootElement.GetProperty("plan").GetString().Should().Be("pro");
        }

        (await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenant.TenantId)).Should().HaveCount(3).And.OnlyContain(e => e.ProfileId == p1.Id);
        (await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "anonymous_id", "a1"))!.Id.Should().Be(p1.Id);
        (await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u1"))!.Id.Should().Be(p1.Id);

        var processed = await TopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, KafkaTopics.EventsProcessed, tenant.TenantId, expected: 5, TimeSpan.FromSeconds(30));
        var merged = processed.Where(r => r.MessageType == nameof(ProfileMerged)).Should().NotBeEmpty().And.Subject.First();
        var payload = JsonSerializer.Deserialize<ProfileMerged>(merged.Value, ContractJson.Options)!;
        payload.WinnerProfileId.Should().Be(p1.Id);
        payload.LoserProfileId.Should().Be(p2.Id);
        merged.Key.Should().Be($"{tenant.TenantId:N}:{p1.Id:N}");
        processed.Should().Contain(r => r.MessageType == nameof(ProfileUpdated));
        // `GET /v1/profiles/{P2}` → 404 is asserted when the read API exists (plan F).
    }

    [Fact]
    public async Task IT009_identity_cache_is_invalidated_on_merge()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = (await TenancySeed.AddApiKeyAsync(_factory, tenant)).Plaintext;
        var redis = stack.Worker.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var cacheKey = RedisKeys.IdentityCache(tenant.TenantId, "anonymous_id", "a1");

        // Here the known user is older, so the anonymous profile P2 is the loser.
        await SendAsync("/v1/track", key, new { userId = "u1", @event = "purchase" });
        var p1 = await ProfileWithEventsAsync(tenant.TenantId, "external_id", "u1", 1);
        await SendAsync("/v1/track", key, new { anonymousId = "a1", @event = "view" });
        var p2 = await ProfileWithEventsAsync(tenant.TenantId, "anonymous_id", "a1", 1);
        await Eventually.TrueAsync(async () => (await redis.StringGetAsync(cacheKey)).ToString() == p2.Id.ToString("N"), "a1 is cached to P2 before the merge");

        await SendAsync("/v1/identify", key, new { userId = "u1", anonymousId = "a1" });
        await Eventually.TrueAsync(
            async () => await ProfilesDb.GetAsync(stack.SqlConnectionString, tenant.TenantId, p2.Id) is { MergedIntoId: not null },
            "the merge commits");

        // The delete happens inside the transaction, so once the merge is visible the key is already gone.
        (await redis.KeyExistsAsync(cacheKey)).Should().BeFalse();

        await SendAsync("/v1/track", key, new { anonymousId = "a1", @event = "view" });
        await Eventually.TrueAsync(async () => (await redis.StringGetAsync(cacheKey)).ToString() == p1.Id.ToString("N"), "the next lookup caches the winner");
        (await ProfileWithEventsAsync(tenant.TenantId, "external_id", "u1", 3)).Id.Should().Be(p1.Id);
    }

    [Fact]
    public async Task IT010_event_count_is_recomputed_after_merge()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = (await TenancySeed.AddApiKeyAsync(_factory, tenant)).Plaintext;

        for (var i = 0; i < 2; i++)
        {
            await SendAsync("/v1/track", key, new { anonymousId = "a1", @event = "view" });
        }

        var p1 = await ProfileWithEventsAsync(tenant.TenantId, "anonymous_id", "a1", 2);
        for (var i = 0; i < 3; i++)
        {
            await SendAsync("/v1/track", key, new { userId = "u1", @event = "purchase" });
        }

        await ProfileWithEventsAsync(tenant.TenantId, "external_id", "u1", 3);

        await SendAsync("/v1/identify", key, new { userId = "u1", anonymousId = "a1" });

        var winner = await Eventually.GetAsync(
            async () => await ProfilesDb.GetAsync(stack.SqlConnectionString, tenant.TenantId, p1.Id) is { EventCount: 5 } w ? w : null,
            "the winner's EventCount is COUNT(*) of its events after the merge");
        winner.ExternalId.Should().Be("u1");
    }
}
```

- [ ] **Step 3: IT-008**

`tests/Crm.IntegrationTests/Profiles/MergeConcurrencyTests.cs`:

```csharp
using Crm.IntegrationTests.Fixtures;
using Crm.Profiles.Application.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

/// <summary>
/// Ruling E15: the handler is called directly. An identify for u1 and tracks for u1 share a Kafka partition, so the
/// broker cannot produce this interleaving on demand; what is proven is the row-lock protocol of spec §4.2.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class MergeConcurrencyTests(CrmStackFixture stack)
{
    private const int ConcurrentTracks = 50;

    [Fact]
    public async Task IT008_events_arriving_during_a_merge_are_not_lost()
    {
        var tenantId = Guid.CreateVersion7(); // schema profiles has no foreign key to identity.Tenants
        var handler = stack.Worker.Services.GetRequiredService<IRawMessageHandler>();
        var t0 = DateTime.UtcNow.AddMinutes(-10);

        var p1 = (await handler.HandleAsync(RawEnvelopes.Track(tenantId, null, "a1", t0), CancellationToken.None)).ProfileId;
        var p2 = (await handler.HandleAsync(RawEnvelopes.Track(tenantId, "u1", null, t0.AddMinutes(1)), CancellationToken.None)).ProfileId;

        var locksTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hold = stack.Hooks.OnMergeLocksTaken(async (tenant, _) =>
        {
            if (tenant == tenantId)
            {
                locksTaken.TrySetResult();
                await release.Task;
            }
        });

        var merge = Task.Run(() => handler.HandleAsync(RawEnvelopes.Identify(tenantId, "u1", "a1", """{"plan":"pro"}""", t0.AddMinutes(2)), CancellationToken.None));
        await locksTaken.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // Both rows are locked and nothing is changed yet. These resolve u1 → P2 (the future loser) and queue on its lock.
        var tracks = Enumerable.Range(0, ConcurrentTracks)
            .Select(i => Task.Run(() => handler.HandleAsync(RawEnvelopes.Track(tenantId, "u1", null, t0.AddMinutes(3).AddSeconds(i)), CancellationToken.None)))
            .ToList();
        await Task.Delay(TimeSpan.FromSeconds(2));
        tracks.Should().OnlyContain(t => !t.IsCompleted, "every applier waits for the merge's row locks");

        release.SetResult();
        var mergeOutcome = await merge.WaitAsync(TimeSpan.FromSeconds(120));
        var outcomes = await Task.WhenAll(tracks).WaitAsync(TimeSpan.FromSeconds(120));

        mergeOutcome.Merged.Should().BeTrue();
        mergeOutcome.ProfileId.Should().Be(p1, "the anonymous profile is older");
        outcomes.Should().OnlyContain(o => o.ProfileId == p1, "an event that raced the merge is applied to the winner");
        outcomes.Should().Contain(o => o.Redirected, "they resolved to P2 before the merge committed and followed MergedIntoId");

        var events = await ProfilesDb.EventsAsync(stack.SqlConnectionString, tenantId);
        events.Should().HaveCount(2 + ConcurrentTracks);
        events.Should().OnlyContain(e => e.ProfileId == p1, "no event references the loser");
        (await ProfilesDb.GetAsync(stack.SqlConnectionString, tenantId, p1))!.EventCount.Should().Be(2 + ConcurrentTracks);
        (await ProfilesDb.GetAsync(stack.SqlConnectionString, tenantId, p2))!.MergedIntoId.Should().Be(p1);
    }
}
```

`IRawMessageHandler.HandleAsync` is one attempt without the retry policy. If SQL Server picks one of the 50 as a deadlock victim (error 1205) the test fails with that exception — that would be a finding about the lock order, not flakiness to retry away; report it with the deadlock graph if you can get one.

- [ ] **Step 4: Run**

With the environment prefix: `dotnet test Crm.sln --filter "Category=Integration&(FullyQualifiedName~MergeTests|FullyQualifiedName~MergeConcurrencyTests)"` → 4 passed. Then the full integration suite once → green. `dotnet build Crm.sln -warnaserror` → 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add tests/Crm.IntegrationTests src tests/Profiles.UnitTests
git commit -m "test(integration): merge through the pipeline — winner, re-pointing, cache invalidation, recount, events racing a merge (IT-007..IT-010)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Monthly partition maintenance

**Files:**
- Create: `src/Modules/Profiles/Profiles.Infrastructure/Maintenance/PartitionBoundaries.cs`, `EventPartitionMaintenanceService.cs`
- Modify: `src/Modules/Profiles/Profiles.Infrastructure/ProfilesWorkerRegistration.cs`
- Test: `tests/Profiles.UnitTests/PartitionBoundariesTests.cs`, `tests/Crm.IntegrationTests/Profiles/PartitionMaintenanceTests.cs`

**Interfaces:**
- Consumes: `SchemaGate`, `ProfilesDbContext`, `IClock`, `PF_Events_Monthly` / `PS_Events_Monthly` (Task 2).
- Produces: `internal static class PartitionBoundaries { static IReadOnlyList<DateTime> Upcoming(DateTime utcNow, int monthsAhead) }`; `internal sealed class EventPartitionMaintenanceService : BackgroundService` with `internal Task<int> EnsureAsync(DateTime utcNow, CancellationToken ct)` (returns how many boundaries it added).

- [ ] **Step 1: Write the failing tests**

`tests/Profiles.UnitTests/PartitionBoundariesTests.cs`:

```csharp
using Crm.Profiles.Infrastructure.Maintenance;
using FluentAssertions;
using Xunit;

namespace Crm.Profiles.UnitTests;

public class PartitionBoundariesTests
{
    [Fact]
    public void Upcoming_is_the_first_instant_of_this_month_and_of_the_months_ahead()
    {
        var boundaries = PartitionBoundaries.Upcoming(new DateTime(2027, 11, 20, 13, 45, 0, DateTimeKind.Utc), monthsAhead: 2);

        boundaries.Should().Equal(
            new DateTime(2027, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
```

`tests/Crm.IntegrationTests/Profiles/PartitionMaintenanceTests.cs`:

```csharp
using Crm.IntegrationTests.Fixtures;
using Crm.Profiles.Infrastructure.Maintenance;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class PartitionMaintenanceTests(CrmStackFixture stack)
{
    [Fact]
    public async Task Missing_month_boundaries_are_added_once()
    {
        var service = stack.Worker.Services.GetServices<IHostedService>().OfType<EventPartitionMaintenanceService>().Single();
        var farFuture = new DateTime(2031, 5, 17, 0, 0, 0, DateTimeKind.Utc); // beyond the migration's static boundaries

        var added = await service.EnsureAsync(farFuture, CancellationToken.None);
        var addedAgain = await service.EnsureAsync(farFuture, CancellationToken.None);

        added.Should().Be(3, "2031-05, 2031-06 and 2031-07 were missing");
        addedAgain.Should().Be(0, "the job is idempotent");
        (await BoundaryExistsAsync(new DateTime(2031, 7, 1))).Should().BeTrue();
    }

    private async Task<bool> BoundaryExistsAsync(DateTime boundary)
    {
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM sys.partition_range_values v
            JOIN sys.partition_functions f ON f.function_id = v.function_id
            WHERE f.name = N'PF_Events_Monthly' AND CAST(v.value AS datetime2(3)) = @boundary
            """;
        command.Parameters.Add(new SqlParameter("@boundary", boundary));
        return (int)(await command.ExecuteScalarAsync())! == 1;
    }
}
```

`EventPartitionMaintenanceService` is internal: add `<InternalsVisibleTo Include="Crm.IntegrationTests" />` to `Profiles.Infrastructure.csproj`.

- [ ] **Step 2: Implement**

`Maintenance/PartitionBoundaries.cs`:

```csharp
namespace Crm.Profiles.Infrastructure.Maintenance;

internal static class PartitionBoundaries
{
    /// <summary>RANGE RIGHT boundaries: the first instant of the current month and of each of the next <paramref name="monthsAhead"/> months.</summary>
    public static IReadOnlyList<DateTime> Upcoming(DateTime utcNow, int monthsAhead)
    {
        var first = new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, monthsAhead + 1).Select(first.AddMonths).ToList();
    }
}
```

`Maintenance/EventPartitionMaintenanceService.cs`:

```csharp
using Crm.Kernel.Time;
using Crm.Profiles.Infrastructure.Consumers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Crm.Profiles.Infrastructure.Maintenance;

/// <summary>
/// docs/data-dictionary/profiles.md: "a maintenance job adds the next month's boundary". OccurredAt is clamped to
/// ReceivedAt ± 7 d, so rows only ever land in the previous, current or next month; keeping two months ahead means a
/// SPLIT always cuts an empty range and is a metadata operation.
/// </summary>
internal sealed class EventPartitionMaintenanceService(IServiceScopeFactory scopes, SchemaGate schemaGate, IClock clock, ILogger<EventPartitionMaintenanceService> logger) : BackgroundService
{
    private const int MonthsAhead = 2;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    internal async Task<int> EnsureAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProfilesDbContext>();
        var added = 0;
        foreach (var boundary in PartitionBoundaries.Upcoming(utcNow, MonthsAhead))
        {
            // One batch per boundary: check and split cannot interleave with another Worker's thanks to the applock.
            var rows = await db.Database.SqlQuery<int>(
                $"""
                BEGIN TRANSACTION;
                EXEC sp_getapplock @Resource = N'PF_Events_Monthly', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
                DECLARE @added int = 0;
                IF NOT EXISTS (
                    SELECT 1 FROM sys.partition_range_values v
                    JOIN sys.partition_functions f ON f.function_id = v.function_id
                    WHERE f.name = N'PF_Events_Monthly' AND CAST(v.value AS datetime2(3)) = {boundary})
                BEGIN
                    ALTER PARTITION SCHEME PS_Events_Monthly NEXT USED [PRIMARY];
                    ALTER PARTITION FUNCTION PF_Events_Monthly() SPLIT RANGE ({boundary});
                    SET @added = 1;
                END
                COMMIT TRANSACTION;
                SELECT @added AS [Value];
                """).ToListAsync(cancellationToken).ConfigureAwait(false);
            added += rows.Single();
        }

        return added;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await schemaGate.WaitAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var added = await EnsureAsync(clock.UtcNow, stoppingToken).ConfigureAwait(false);
                    if (added > 0)
                    {
                        logger.LogInformation("Added {Count} monthly boundary(ies) to PF_Events_Monthly", added);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not urgent: rows beyond the last boundary still land in the last partition.
                    logger.LogError(ex, "Partition maintenance failed; next attempt in {Interval}", Interval);
                }

                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }
}
```

`SqlQuery<int>` composes a `SELECT` around raw SQL when LINQ operators follow, which a multi-statement batch cannot take — hence `ToListAsync()` and `Single()` in memory. If EF still refuses the batch (it must start with `SELECT` for `SqlQuery` on some providers), fall back to `db.Database.GetDbConnection()` + `DbCommand.ExecuteScalarAsync` with a `@boundary` parameter, opening the connection through `db.Database.OpenConnectionAsync`.

`ProfilesWorkerRegistration.cs` — add `services.AddHostedService<EventPartitionMaintenanceService>();` after the consumer service (plus `using Crm.Profiles.Infrastructure.Maintenance;`).

- [ ] **Step 3: Run**

`dotnet test tests/Profiles.UnitTests` → PASS. With the environment prefix: `dotnet test Crm.sln --filter "Category=Integration&(FullyQualifiedName~PartitionMaintenanceTests|FullyQualifiedName~Profiles.SchemaTests)"` → green (SchemaTests asserts "at least 24 boundaries", so added ones do not break it). `dotnet build Crm.sln -warnaserror` → 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Modules/Profiles/Profiles.Infrastructure tests/Profiles.UnitTests tests/Crm.IntegrationTests/Profiles/PartitionMaintenanceTests.cs
git commit -m "feat(worker): keep PF_Events_Monthly two months ahead

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Documentation — spec rulings, data dictionary, test-case tags, developer notes

**Files:**
- Modify: `docs/superpowers/specs/2026-09-15-crm-slice1-design.md` (§4.2, §4.3), `docs/data-dictionary/profiles.md`, `docs/data-dictionary/redis-and-kafka.md`, `docs/test/unit.md`, `docs/test/integration.md`, `docs/test/traceability.md` (only if it lists file paths or states), `docs/dev-setup.md`, `docs/design/architecture.md` (only where it describes the Worker), `README.md` (status paragraph, if it names what is implemented)

**Interfaces:** none (documentation). `make docs-check` must end at `0 errors`; it fails on a `Planned:` bullet whose path exists.

- [ ] **Step 1: Spec** — after §4.2's numbered "Semantics" list add one paragraph **"Rulings (plan E)."** that states, in prose and in this order: E2 (traits applied in the domain under the row lock; JSON `null` removes a key), E3 (`FirstSeenAt`/`LastSeenAt` from `ReceivedAt`, never backwards), E4 (`OccurredAt` truncated to ms and clamped before use, and why), E5 (track never merges; promotion of an anonymous profile; no merge when the anonymous profile already has an external id), E6 (email/phone promotion without merging), E7 (**replace** the sentence in step 4 "Duplicate `Id` … → treat as already processed, skip." with: "Duplicate `(Id, OccurredAt)` → no SQL write; `EventTracked` is published again, so `events.processed` is at-least-once and consumers dedupe by `eventId`" and describe the remaining `ProfileUpdated`/`ProfileMerged` crash window and the outbox as the future fix), E10 (consumer instances, per-message synchronous commit), E11 (schema gate), E12 (Redis failures are misses; cache writes after commit, deletes before). In §4.3 replace "Polly retry 3× exponential (200 ms → 2 s)" with "three attempts with waits of 200 ms and 2 s (configurable; a plain loop, no Polly)", add that **every** exception is retried (E8) and add the bullet for E9 (failures after the SQL commit rebuild the consumer; never dead-lettered). In §8.1 change "Worker uses Polly retry, then DLQ" to "Worker retries, then DLQ". Use the rulings table at the top of this plan as the source; do not paste the table.

- [ ] **Step 2: Data dictionary** — `profiles.md`: under Profiles add the constraint `FK_Profiles_MergedIntoId` is created without a supporting index (merge losers are never looked up by it); under UserIdentities note that the default database collation makes `Value` case-insensitive in SQL while the Redis key is case-sensitive (a differently-cased id is a cache miss that resolves to the same profile); under Events state that there is no `PRIMARY KEY` constraint — `UX_Events_Id` is the key EF uses — that `FK_Events_ProfileId` exists without its own index, that `PF_Events_Monthly` ships boundaries 2026-01 … 2027-12 and `EventPartitionMaintenanceService` keeps the current month + 2 present, and that `OccurredAt` is stored truncated to milliseconds. `redis-and-kafka.md`: in the identity-cache row set "Written by" to "Raw events consumer after the SQL commit, on a lookup miss or a new identity; deleted inside the merge transaction for the loser's identities"; in the Kafka section add the sentence that `events.processed` is at-least-once (`EventTracked` is re-announced for a redelivered duplicate) and that consumer settings are `enable.auto.commit=false`, `auto.offset.reset=earliest`, one synchronous commit per record.

- [ ] **Step 3: Test specifications** — `docs/test/unit.md`: UT-009 … UT-015 `Planned:` → `Implemented:` (paths already match the files of Task 1). `docs/test/integration.md`: IT-002 → `Implemented: tests/Crm.IntegrationTests/Ingestion/IdentifyTests.cs` and replace its "Plan D note" with "Plan E note: asserted through SQL; the API view of the profile arrives with plan F."; IT-004, IT-005, IT-006, IT-009, IT-010 → `Implemented:`; IT-005's Then-line: "after three retries" wording stays, `attempt = 3` stays; IT-004 Then-line: append "; `EventTracked` is announced again (at-least-once, ruling E7)"; IT-006 Then-line: append "; the redelivery publishes the `EventTracked` the crashed delivery never sent"; IT-007 → `Implemented:` plus "Plan E note: the `GET /v1/profiles/{P2}` → 404 assertion arrives with plan F."; IT-008 → `Implemented:` and rewrite Given/When to what the test does: "Given profiles P1 (`a1`, older) and P2 (`u1`) and a merge in flight (transaction held open by a test hook after both row locks are taken) — When 50 tracks for `u1` are handed to the Worker's message handler concurrently (direct calls; Kafka would serialize them behind the identify on one partition) — Then …" (Then unchanged); IT-001: extend its "Plan D note" with "Plan E: `tests/Crm.IntegrationTests/Ingestion/TrackPipelineTests.cs` proves track → `profiles.Events` → `events.processed`; the `GET` half arrives with plan F." and keep `Planned:`. Add to the file's introduction one sentence: the Worker runs once per test run inside the fixture with `SwitchableHooks`.

- [ ] **Step 4: Developer notes** — `docs/dev-setup.md`: `make migrate` now applies Tenancy and Profiles; the Worker waits for schema `profiles` and logs "Waiting for schema 'profiles'" until the API (Development) or `make migrate` created it; configuration keys `Profiles:Consumer:{Enabled,GroupId,Instances,RetryDelaysMs,RestartDelayMs}` with defaults; how to look at dead letters: `docker exec crm-kafka-1 /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic events.dlq --from-beginning --property print.headers=true --max-messages 5` (verify the container name and script path against `deploy/docker-compose.yml` before writing it). `docs/design/architecture.md` and `README.md`: update only statements that are now wrong (for example "the Worker has no consumers yet").

- [ ] **Step 5: Verify and commit**

Run: `make docs-check` → `0 errors`; `python3 -m unittest discover -s tools/tests` → OK.

```bash
git add docs README.md
git commit -m "docs: plan E rulings in the spec, profiles schema notes, implemented test cases tagged, Worker developer notes

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: Live verification (controller-run)

Not dispatched to a subagent. On the engine (`make tunnel`, `DOCKER_HOST` exported in **every** shell call, overrides on the make command line):

- [ ] `make up-observability MSSQL_HOST_PORT=11433 PROMETHEUS_HOST_PORT=19090 MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest DOTNET_SYSTEM_NET_DISABLEIPV6=1`, `make tunnel-observability PROMETHEUS_HOST_PORT=19090`, `make seed`.
- [ ] Worker log shows "Waiting for schema 'profiles'" at most briefly, then four consumers joined; `/health/ready` on 8081 is 200.
- [ ] README quickstart track → within 2 s a row in `profiles.Events` (query through `docker exec crm-sqlserver-1 /opt/mssql-tools18/bin/sqlcmd …`), profile `EventCount = 1`; identify with `anonymousId` after anonymous tracks → loser row has `MergedIntoId`, events re-pointed.
- [ ] Hand-produced malformed record → appears on `events.dlq` with the six headers; Prometheus `crm_dlq_total{topic="events.raw"}` = 1; Grafana panels "Consumer lag" and "DLQ" show series; Jaeger shows one trace spanning `POST /v1/track` → `events.raw publish` → `events.raw process`.
- [ ] Re-run `tests/Crm.LoadTests/coldstart.sh …` (after `make down`): budgets still hold with the Worker consuming; update the README row only if the verdict changes.

## Exit gate

- `dotnet build Crm.sln -warnaserror` → 0 warnings; `dotnet test Crm.sln --filter "Category!=Integration"` → green; integration suite → green, 0 skipped; `python3 -m unittest discover -s tools/tests` → OK; `make docs-check` → 0 errors; `make compose-check` → two OK lines; `git diff --stat docs/api/openapi.json` against the plan's first commit → empty.
- Final whole-change review (Opus) over the plan's commit range, one fix wave, scoped re-review.
- Task 10's live checks pass on the engine.

## Self-review notes

- **Spec coverage:** §4.2 steps 1–5 → Tasks 3–5 (resolve, merge, upsert, append, publish, offset); §4.2 lock protocol items 1–3 → `EfProfileStore.LockProfileAsync`, `WinnerResolver`, `Forget` before commit, proven by IT-008/IT-009; §4.3 retry, DLQ, no crash loop → `MessageRetryPolicy`, `DeadLetterPublisher`, consumer rebuild loop, IT-005/IT-006; §3.1 schema → Task 2 + SchemaTests; §3.4 identity cache → `RedisIdentityCache`; §3.5 topics/headers → publishers; §2.5 tenant scope per message and header = payload → `ScopedMessageHandler`, `RawMessageDecoder`; §8.2 `crm_consumer_lag`, `crm_dlq_total`, trace propagation → `ProfilesMetrics`, `StartActivity`. Partition maintenance (data dictionary) → Task 8.
- **Deliberately not done (plan F or later):** read API and its 404 for merged profiles; IT-001 GET half, IT-011, IT-012, IT-040; seed volume and LT-005 at volume; transactional outbox; commit batching / throughput tuning (load-test plan); merging by e-mail or phone; per-tenant promoted attribute lists; a DLQ replay tool; Serilog `ProfileId` enricher.
- **Type consistency check:** `IPipelineHooks.MergeLocksTakenAsync(Guid, CancellationToken)` / `SqlCommittedAsync(RawEnvelope, CancellationToken)` are used with those shapes in `ProfilePipeline`, `RawEventsConsumerService`, `SwitchableHooks` and the unit tests; `IProfileStore` members used by `ProfilePipeline` = members implemented by `InMemoryProfileStore` and `EfProfileStore`; `ProcessingOutcome(Messages, ProfileId, Duplicate, Redirected, Merged)` is constructed positionally in one place (`ProfilePipeline`).


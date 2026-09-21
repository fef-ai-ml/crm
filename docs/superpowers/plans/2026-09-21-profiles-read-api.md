# Profiles Read API and Seed Volume Implementation Plan (plan F)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the profiles the Worker builds — `GET /v1/profiles?q=`, `GET /v1/profiles/{id}`, `GET /v1/profiles/{id}/events` with keyset cursors — and load the documented 5,000-profile / 100,000-event demo dataset with `make seed` inside the LT-005 budget.

**Architecture:** Query contracts (DTOs, `IProfileQueries`, search-term normalization, error catalogue) live in `Profiles.Application`; the EF Core implementation and the minimal-API endpoints live in `Profiles.Infrastructure`; the API host secures the group (`AdminOrViewer`, tag `Profiles`) because a module may not reference Tenancy (AT-001). The cursor codec and the `limit` rule are Kernel types so Segmentation inherits them. The seed dataset is a pure, deterministic, quota-based generator (`Profiles.Application/Seeding`) plus a `SqlBulkCopy` loader (`Profiles.Infrastructure/Seeding`) behind a Development-only endpoint that `tools/seed.py` calls; integration tests call the loader directly.

**Tech Stack:** .NET 10 minimal APIs, EF Core 10 (SQL Server 2022), `System.Buffers.Text.Base64Url`, `Microsoft.Data.SqlClient.SqlBulkCopy`, xUnit + FluentAssertions 7 + Testcontainers, Python 3 standard library (`tools/seed.py`).

**Spec:** `docs/superpowers/specs/2026-09-15-crm-slice1-design.md` (§3.1 data model, §6 API surface, §7 Web expectations, "Rulings (plan E)"); contract docs `docs/api/README.md` (§Pagination, §Profiles), `docs/brd/BRD.md` FR-09/FR-11, test cases `docs/test/integration.md` IT-001/007/011/012/030/040, `docs/test/e2e-load.md` LT-005, `docs/test/fixtures.md` (seed dataset).

## Global Constraints

- Work on `main`; one commit per task unless the task says otherwise; every commit message ends with a blank line and exactly `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Commits are SSH-signed through 1Password: never pass `--no-gpg-sign` or `-c commit.gpgsign=false`; if `git commit` fails or hangs ~60 s, leave everything staged and report `DONE_UNCOMMITTED` — do not retry in a loop.
- `dotnet build Crm.sln -warnaserror` must end with 0 warnings. Known build-error classes here: CS1573 (document all parameters or none), CA1873 (guard expensive log arguments with `IsEnabled`), any `[Obsolete]` API. No `.ConfigureAwait(false)` (repo convention).
- Prefix **every** `dotnet` command with `DOTNET_SYSTEM_NET_DISABLEIPV6=1` (this Mac's IPv6 route is broken; a real NuGet restore otherwise stalls at 0 % CPU). One `dotnet` command at a time, always in the foreground, never `rm -rf obj bin`.
- Integration tests run against the remote engine: every shell call exports `DOCKER_HOST=unix://$HOME/.local/share/containers/home-lab.sock TESTCONTAINERS_HOST_OVERRIDE=home-lab TESTCONTAINERS_RYUK_DISABLED=true MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest`. "Cannot connect to the Docker daemon" → report `BLOCKED` (the controller restores the tunnel).
- Forbidden for implementers and reviewers: reading or editing `deploy/.env`, `.env.local`, `~/.config`, `~/.local`; `podman machine`, `docker context …`, `make up*`, `make down`, `make tunnel*`, `make seed`, `make migrate`, `pkill`, volume removal, prune, `coldstart.sh`; `git stash/restore/reset/checkout --/clean`; committing `.superpowers/` or `.env.local`; `curl`/`wget` (use `python3` + `urllib`).
- Module boundaries (arch tests AT-001…AT-004): `Crm.Profiles.*` references only `Crm.Kernel` and `Crm.Contracts`; policy names, rate-limit names and endpoint tags are Tenancy constants applied by the host.
- Handlers return `Task<object>` — an `IResult` or a Kernel `Error` (rendered by `ErrorResultFilter` on the `/v1` group). Every endpoint declares its success body with `.Produces<T>()`; `401`/`403` are added by `CrmOperationTransformer`, never by hand.
- Every profile read adds `MergedIntoId == null` itself; the tenant predicate comes from the global query filter, except in raw SQL, which always carries `TenantId = @tenant`.
- `docs/api/openapi.json` is regenerated with `make openapi` and committed in the same commit as the endpoint change (CI diffs it); `make docs-check` stays at 0 errors: a `- Planned: <path>` bullet whose file now exists becomes `- Implemented: <path>`.
- Where code in this plan disagrees with an existing signature in the tree (a member name, a namespace), the tree wins; adapt and say so in the report. Where it disagrees with a ruling below, the ruling wins.

## Rulings

| Id | Ruling | Why | Cost if wrong |
|---|---|---|---|
| F1 | Scope: the three read endpoints, IT-001 GET half, IT-007's 404, IT-011, IT-012, IT-030's profile read, IT-040, the seed dataset, LT-005 at volume. Not here: Web pages, `/v1/segments`, deleting or repointing the `/v1/_test` probes (bound to the Segmentation plan by P4/D12), LT-002's k6 script (load-test plan). | Smallest slice that makes profiles visible and closes LT-005. | None; later plans are already scheduled. |
| F2 | Cursor = Base64Url of `1\|<scope>\|<ticks>\|<id:N>` (`KeysetCursor` in `Crm.Kernel.Paging`), scope `profiles` or `events`. Not tenant-bound: it only positions a keyset inside a query that is already tenant-filtered. Malformed, wrong scope, or longer than 256 chars → `400 pagination.invalid_cursor`. | First cursor in the codebase; Segmentation reuses it. Opaque to clients, debuggable by us. | A format change later invalidates outstanding cursors (they live for seconds). |
| F3 | `limit`: default 50, 1…200; outside → `400 pagination.invalid_limit` (no silent clamp). | The docs state the range; a clamp hides client bugs. | A client that sends 500 gets a 400 instead of 200 rows. |
| F4 | Timeline order is `OccurredAt DESC, Id DESC`; search order is `LastSeenAt DESC, Id DESC`. `Id` only breaks ties; comparison and ordering both happen in SQL Server, so its `uniqueidentifier` ordering is consistent with itself. An event with an old client timestamp sorts where that timestamp puts it (documented). | "Newest first" in the docs means event time; `OccurredAt` is not unique (E4). | IT-001/LT-002 send current timestamps, so they are unaffected. |
| F5 | Keyset predicate uses `x.Id.CompareTo(cursorId) < 0`, which EF Core translates. If the first integration test shows it does not translate (`InvalidOperationException … could not be translated`), replace only that predicate with `FromSqlInterpolated` over the same table (the tenant filter still composes) and note it in the report. | Avoid raw SQL where LINQ works. | One extra fix inside Task 3. |
| F6 | `q` is optional. Absent or blank → all live profiles of the tenant, `lastSeenAt` desc. Present → trimmed, ≤ 256 chars (else `400 profiles.invalid_query`), matched as a **prefix** against `ExternalId`, `Email` and — when `q` minus ` -.()` is `+?` followed by 1–15 digits — `Phone` as `+digits%`. LIKE metacharacters `\ % _ [` are escaped. Case-insensitivity comes from the database's default collation (data dictionary). Anonymous ids are not searchable. A `+` that arrived as a space (unencoded query string) is covered by the optional `+`. | FR-11, IT-011; `IdentityKey.Phone/Email` reject prefixes, so search has its own normalizer. | A digits-only external-id search also probes phones: harmless OR. |
| F7 | Search items are thin: `id, externalId, email, phone, lastSeenAt, eventCount`. Only `GET /v1/profiles/{id}` returns `attributes` and `firstSeenAt`. | `Attributes` is uncapped (plan E I3); spec §7's table needs four columns; segment members are thin too. | The docs change in the same commit; no client exists yet. |
| F8 | Missing, foreign-tenant and merged profiles are indistinguishable: `404 profile.not_found` "Profile not found." on both `/{id}` and `/{id}/events`. | FR-09 "excluded from all reads"; no existence leak. | A client cannot follow a merge through the API (not a requirement). |
| F9 | Timeline filters: `name` must match `^[A-Za-z0-9_]{1,64}$` (else `400 profiles.invalid_filter`); `from` inclusive, `to` exclusive, bound as `DateTimeOffset`, `from >= to` → `400 profiles.invalid_filter`. | Same name rule as ingest; half-open ranges compose. | — |
| F10 | No rate limiter on the read endpoints (documented as such). | None is specified; LT-002 polls the timeline under load. | Reads are JWT-only; abuse needs a login. |
| F11 | Response timestamps are UTC with a `Z` suffix: every `DateTime` read from SQL is passed through `DateTime.SpecifyKind(…, Utc)` before it is returned. `attributes`/`properties` are `JsonElement` (parsed from the stored JSON text). | EF materializes `Kind=Unspecified`; the docs show objects, not strings. | — |
| F12 | The seed dataset is loaded by SQL bulk copy, not through the pipeline: 100k events at one synchronous Kafka commit each cannot fit the 120 s `make seed` budget. The 50 merge losers are written as the pipeline would have left them (loser row with `MergedIntoId`, no contact points; anonymous identity on the winner). | LT-005 budget. | The dataset does not exercise the pipeline — integration tests already do. |
| F13 | The generator is quota-based, not sampled: a fixed cell table (plan × country × dormant) reproduces the six documented counts exactly (412 / 2,731 / 1,500 / 500 / 4,000 / 187); the PRNG (SplitMix64, seed `20260915`) only shuffles and fills. Event times are stored as ages relative to load time, so the counts hold at load time and drift afterwards (dormancy grows) — documented. `ltv` is right-skewed, not log-normal (doc updated). | The documented counts were fixed before any generator existed; sampling cannot hit them. | A distribution change means editing the cell table and the doc table together. |
| F14 | Profile `u-00001` (`alice.1@example.test`) is pinned: pro, ID, active, purchaser, exactly 120 events on top of its mandatory ones. | E2E-006 needs a profile matching `alice` with more than 50 events. | — |
| F15 | The loader verifies the six counts with hand-written SQL inside the transaction and rolls back on a mismatch (`seed.count_mismatch`). Re-running is a no-op that reports `exists` (detected by external id `u-00001` in the tenant). The endpoint `POST /v1/dev/seed-dataset` is Development-only, `AdminOnly`, excluded from OpenAPI. | `fixtures.md`: "the seeder asserts these counts"; `make seed` must stay idempotent. | The SQL is superseded by the rule compiler later; IT-014 will cross-check. |
| F16 | IT-040 is asserted with an in-process `ActivityListener` over all sources, classifying spans by `ActivityKind` and tags, not by source name. | Source names are an implementation detail; the case is about one shared trace id. | — |
| F17 | IT-030 gets its profile-read assertion now; its segment assertions stay with the Segmentation plan (doc note). | The case is tagged Implemented but has no test. | — |

## File map

| Path | Responsibility |
|---|---|
| `src/Shared/Crm.Kernel/Paging/KeysetCursor.cs`, `PageLimit.cs`, `PagingErrors.cs` | cursor codec, limit rule, shared error codes |
| `src/Modules/Profiles/Profiles.Application/Queries/ProfileQueryModels.cs` | response and filter records |
| `…/Queries/IProfileQueries.cs`, `ProfileSearchTerm.cs`, `ProfilesErrors.cs` | query port, `q` normalizer + LIKE escaping, error catalogue |
| `src/Modules/Profiles/Profiles.Infrastructure/Queries/EfProfileQueries.cs` | EF Core implementation |
| `…/Endpoints/ProfileEndpoints.cs`, `ProfilesEndpointExtensions.cs` | the three GETs; `MapProfilesEndpoints`, `MapProfilesDevSeed` |
| `src/Modules/Tenancy/Tenancy.Infrastructure/Auth/AuthSchemes.cs` | `EndpointTags.Profiles` |
| `src/Hosts/Crm.Api/Program.cs` | secured group for profiles; Development-only seed group |
| `src/Modules/Profiles/Profiles.Application/Seeding/SeedPrng.cs`, `SeedModels.cs`, `SeedDatasetGenerator.cs`, `SeedExpectations.cs` | pure deterministic generator and the six expected counts |
| `src/Modules/Profiles/Profiles.Infrastructure/Seeding/SeedDatasetLoader.cs`, `SeedEndpoints.cs` | bulk load + verification; Development endpoint |
| `tools/seed.py`, `tools/tests/test_seed.py` | call the endpoint, report, tests |
| `tests/Crm.Kernel.UnitTests/Paging/*`, `tests/Profiles.UnitTests/Queries/*`, `tests/Profiles.UnitTests/Seeding/*` | unit tests |
| `tests/Crm.IntegrationTests/Profiles/{ProfilesClient,ProfileReadTests,SearchTests,TimelineTests,SeedDatasetTests}.cs`, `Ingestion/TrackRoundTripTests.cs`, `Ops/ObservabilityTests.cs`, `Fixtures/SeedDataset.cs`, edits to `Profiles/MergeTests.cs`, `Tenancy/IsolationTests.cs` | integration tests |
| `tests/Crm.Api.UnitTests/OpenApiDocumentTests.cs`, `docs/api/openapi.json` | contract |
| docs: `docs/api/README.md`, spec, `docs/test/{integration,e2e-load,fixtures}.md`, `docs/data-dictionary/profiles.md`, `docs/dev-setup.md`, `README.md`, `tests/Crm.LoadTests/coldstart.sh` header | Task 7 |

---

### Task 1: Kernel paging — cursor codec and limit rule

**Files:**
- Create: `src/Shared/Crm.Kernel/Paging/KeysetCursor.cs`, `src/Shared/Crm.Kernel/Paging/PageLimit.cs`, `src/Shared/Crm.Kernel/Paging/PagingErrors.cs`
- Test: `tests/Crm.Kernel.UnitTests/Paging/KeysetCursorTests.cs`, `tests/Crm.Kernel.UnitTests/Paging/PageLimitTests.cs`

**Interfaces:**
- Consumes: `Crm.Kernel.Error`, `Crm.Kernel.Result<T>` (`IsFailure`, `Error`, `Value`, implicit conversions from `T` and `Error`).
- Produces: `KeysetCursor(DateTime Timestamp, Guid Id)` with `string Encode(string scope)` and `static bool TryDecode(string scope, string? text, out KeysetCursor cursor)`; `PageLimit.Default = 50`, `PageLimit.Max = 200`, `static Result<int> PageLimit.Resolve(int? limit)`; `PagingErrors.InvalidLimit`, `PagingErrors.InvalidCursor` (codes `pagination.invalid_limit`, `pagination.invalid_cursor`).

- [ ] **Step 1: Write the failing tests**

`tests/Crm.Kernel.UnitTests/Paging/KeysetCursorTests.cs`:

```csharp
using Crm.Kernel.Paging;
using FluentAssertions;

namespace Crm.Kernel.UnitTests.Paging;

public sealed class KeysetCursorTests
{
    private static readonly DateTime At = new(2026, 9, 21, 8, 30, 15, 123, DateTimeKind.Utc);
    private static readonly Guid Id = Guid.Parse("01a0c18d-2cd6-7d61-941f-e4133dcc6417");

    [Fact]
    public void Round_trips_timestamp_and_id_as_utc()
    {
        var text = new KeysetCursor(At, Id).Encode("events");

        KeysetCursor.TryDecode("events", text, out var cursor).Should().BeTrue();
        cursor.Timestamp.Should().Be(At);
        cursor.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        cursor.Id.Should().Be(Id);
    }

    [Fact]
    public void Encoded_text_is_url_safe()
        => new KeysetCursor(At, Id).Encode("profiles").Should().MatchRegex("^[A-Za-z0-9_-]+$");

    [Fact]
    public void Unspecified_kind_round_trips_by_ticks()
    {
        var unspecified = DateTime.SpecifyKind(At, DateTimeKind.Unspecified);
        KeysetCursor.TryDecode("events", new KeysetCursor(unspecified, Id).Encode("events"), out var cursor).Should().BeTrue();
        cursor.Timestamp.Ticks.Should().Be(At.Ticks);
    }

    [Fact]
    public void Wrong_scope_is_rejected()
        => KeysetCursor.TryDecode("profiles", new KeysetCursor(At, Id).Encode("events"), out _).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 !!")]
    [InlineData("aGVsbG8")]                          // "hello": no separators
    [InlineData("MnxldmVudHN8MXwwMA")]                // version 2
    [InlineData("MXxldmVudHN8YWJjfDAw")]              // ticks not a number
    [InlineData("MXxldmVudHN8LTV8MDFhMGMxOGQyY2Q2N2Q2MTk0MWZlNDEzM2RjYzY0MTc")] // negative ticks
    public void Malformed_text_is_rejected(string? text)
        => KeysetCursor.TryDecode("events", text, out _).Should().BeFalse();

    [Fact]
    public void Oversized_text_is_rejected_without_decoding()
        => KeysetCursor.TryDecode("events", new string('A', 257), out _).Should().BeFalse();
}
```

`tests/Crm.Kernel.UnitTests/Paging/PageLimitTests.cs`:

```csharp
using Crm.Kernel.Paging;
using FluentAssertions;

namespace Crm.Kernel.UnitTests.Paging;

public sealed class PageLimitTests
{
    [Fact]
    public void Absent_limit_is_the_default() => PageLimit.Resolve(null).Value.Should().Be(50);

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(200)]
    public void Limits_inside_the_range_pass_through(int limit) => PageLimit.Resolve(limit).Value.Should().Be(limit);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public void Limits_outside_the_range_are_a_validation_error(int limit)
    {
        var result = PageLimit.Resolve(limit);

        result.IsFailure.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("pagination.invalid_limit");
        result.Error.Fields.Should().ContainKey("limit");
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test tests/Crm.Kernel.UnitTests --filter "FullyQualifiedName~Paging"`
Expected: build error CS0234 — namespace `Crm.Kernel.Paging` does not exist.

- [ ] **Step 3: Implement**

`src/Shared/Crm.Kernel/Paging/PagingErrors.cs`:

```csharp
namespace Crm.Kernel.Paging;

/// <summary>Errors every paginated endpoint shares (docs/api/README.md, Pagination).</summary>
public static class PagingErrors
{
    public const string InvalidLimitCode = "pagination.invalid_limit";
    public const string InvalidCursorCode = "pagination.invalid_cursor";

    public static readonly Error InvalidLimit = Error.Validation(
        InvalidLimitCode,
        $"limit must be between 1 and {PageLimit.Max}",
        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["limit"] = [$"must be between 1 and {PageLimit.Max}"] });

    public static readonly Error InvalidCursor = Error.Validation(
        InvalidCursorCode,
        "cursor is not a value returned by this endpoint",
        new Dictionary<string, string[]>(StringComparer.Ordinal) { ["cursor"] = ["is not a value returned by this endpoint"] });
}
```

`src/Shared/Crm.Kernel/Paging/PageLimit.cs`:

```csharp
namespace Crm.Kernel.Paging;

/// <summary>`limit` of every list endpoint: default 50, 1 to 200; anything else is a 400, never a silent clamp (ruling F3).</summary>
public static class PageLimit
{
    public const int Default = 50;
    public const int Max = 200;

    public static Result<int> Resolve(int? limit)
    {
        if (limit is null)
        {
            return Default;
        }

        if (limit is < 1 or > Max)
        {
            return PagingErrors.InvalidLimit;
        }

        return limit.Value;
    }
}
```

`src/Shared/Crm.Kernel/Paging/KeysetCursor.cs`:

```csharp
using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Crm.Kernel.Paging;

/// <summary>
/// Position in a keyset-ordered list: the sort timestamp and the row id that breaks ties. On the wire it is
/// Base64Url("1|scope|ticks|id") — opaque to clients, not tenant-bound (it only positions a query that is already
/// tenant-filtered), and rejected when the scope is another endpoint's (ruling F2).
/// </summary>
public readonly record struct KeysetCursor(DateTime Timestamp, Guid Id)
{
    private const string Version = "1";
    private const int MaxTextLength = 256;

    public string Encode(string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        var plain = string.Create(CultureInfo.InvariantCulture, $"{Version}|{scope}|{Timestamp.Ticks}|{Id:N}");
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(plain));
    }

    public static bool TryDecode(string scope, string? text, out KeysetCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            return false;
        }

        string plain;
        try
        {
            plain = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(text));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = plain.Split('|');
        if (parts.Length != 4
            || parts[0] != Version
            || !string.Equals(parts[1], scope, StringComparison.Ordinal)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(parts[3], "N", out var id))
        {
            return false;
        }

        cursor = new KeysetCursor(new DateTime(ticks, DateTimeKind.Utc), id);
        return true;
    }
}
```

(`NumberStyles.None` rejects a leading `-`, which is the negative-ticks test case.)

- [ ] **Step 4: Run the tests**

Run: `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test tests/Crm.Kernel.UnitTests`
Expected: all pass (22 existing + 20 new). If an `[InlineData]` literal turns out to decode differently from its comment, fix the literal (build it with `Convert.ToBase64String(Encoding.UTF8.GetBytes("…")).TrimEnd('=')`), not the codec.

- [ ] **Step 5: Commit**

```bash
git add src/Shared/Crm.Kernel/Paging tests/Crm.Kernel.UnitTests/Paging
git commit -m "feat(kernel): keyset cursor codec and the list limit rule"
```

---

### Task 2: Profiles query contracts, search-term normalizer, error catalogue

**Files:**
- Create: `src/Modules/Profiles/Profiles.Application/Queries/ProfileQueryModels.cs`, `IProfileQueries.cs`, `ProfileSearchTerm.cs`, `ProfilesErrors.cs` (same directory)
- Test: `tests/Profiles.UnitTests/Queries/ProfileSearchTermTests.cs`, `tests/Profiles.UnitTests/Queries/TimelineFilterTests.cs`

**Interfaces:**
- Consumes: `Crm.Kernel.Result<T>`, `Crm.Kernel.Error`.
- Produces (namespace `Crm.Profiles.Application.Queries`):
  - `ProfileListItem(Guid Id, string? ExternalId, string? Email, string? Phone, DateTime LastSeenAt, long EventCount)`, `ProfileListResponse(IReadOnlyList<ProfileListItem> Items, string? NextCursor)`
  - `ProfileResponse(Guid Id, string? ExternalId, string? Email, string? Phone, JsonElement Attributes, DateTime FirstSeenAt, DateTime LastSeenAt, long EventCount)`
  - `TimelineEvent(Guid Id, string Name, JsonElement Properties, DateTime OccurredAt, DateTime ReceivedAt)`, `TimelineResponse(IReadOnlyList<TimelineEvent> Items, string? NextCursor)`
  - `TimelineFilter(DateTime? FromUtc, DateTime? ToUtc, string? Name)` with `static Result<TimelineFilter> Create(DateTimeOffset? from, DateTimeOffset? to, string? name)`
  - `ProfileSearchTerm` with `bool IsEmpty`, `string TextPattern`, `string? PhonePattern`, `const string LikeEscape = "\\"`, `static Result<ProfileSearchTerm> Parse(string? q)`
  - `IProfileQueries` with `SearchAsync(string? q, int? limit, string? cursor, CancellationToken)`, `GetAsync(Guid id, CancellationToken)`, `TimelineAsync(Guid id, TimelineFilter filter, int? limit, string? cursor, CancellationToken)`
  - `ProfilesErrors.NotFound` (`profile.not_found`), `ProfilesErrors.InvalidQuery(string message)` (`profiles.invalid_query`, field `q`), `ProfilesErrors.InvalidFilter(string field, string message)` (`profiles.invalid_filter`)

- [ ] **Step 1: Write the failing tests**

`tests/Profiles.UnitTests/Queries/ProfileSearchTermTests.cs`:

```csharp
using Crm.Profiles.Application.Queries;
using FluentAssertions;

namespace Crm.Profiles.UnitTests.Queries;

public sealed class ProfileSearchTermTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_q_means_browse_everything(string? q)
        => ProfileSearchTerm.Parse(q).Value.IsEmpty.Should().BeTrue();

    [Fact]
    public void Text_is_trimmed_and_becomes_a_prefix_pattern()
    {
        var term = ProfileSearchTerm.Parse("  ali ").Value;

        term.IsEmpty.Should().BeFalse();
        term.TextPattern.Should().Be("ali%");
        term.PhonePattern.Should().BeNull();
    }

    [Theory]
    [InlineData("50%_off", @"50\%\_off%")]
    [InlineData(@"a\b", @"a\\b%")]
    [InlineData("x[1]", @"x\[1]%")]
    public void Like_metacharacters_are_escaped(string q, string expected)
        => ProfileSearchTerm.Parse(q).Value.TextPattern.Should().Be(expected);

    [Theory]
    [InlineData("+62812", "+62812%")]
    [InlineData("62812", "+62812%")]              // '+' lost to query-string decoding (ruling F6)
    [InlineData("+62 812-345 (6)", "+628123456%")]
    [InlineData("+6.2", "+62%")]
    public void Phone_like_text_also_gets_a_phone_pattern(string q, string expected)
        => ProfileSearchTerm.Parse(q).Value.PhonePattern.Should().Be(expected);

    [Theory]
    [InlineData("ALI-9")]
    [InlineData("+62a")]
    [InlineData("++62")]
    [InlineData("1234567890123456")]              // 16 digits: not E.164
    [InlineData("-")]
    public void Other_text_has_no_phone_pattern(string q)
        => ProfileSearchTerm.Parse(q).Value.PhonePattern.Should().BeNull();

    [Fact]
    public void Text_longer_than_256_characters_is_a_validation_error()
    {
        var result = ProfileSearchTerm.Parse(new string('a', 257));

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("profiles.invalid_query");
        result.Error.Fields.Should().ContainKey("q");
    }

    [Fact]
    public void Text_of_exactly_256_characters_is_accepted()
        => ProfileSearchTerm.Parse(new string('a', 256)).IsSuccess.Should().BeTrue();
}
```

`tests/Profiles.UnitTests/Queries/TimelineFilterTests.cs`:

```csharp
using Crm.Profiles.Application.Queries;
using FluentAssertions;

namespace Crm.Profiles.UnitTests.Queries;

public sealed class TimelineFilterTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 21, 12, 0, 0, TimeSpan.FromHours(7));

    [Fact]
    public void Absent_filters_are_valid()
    {
        var filter = TimelineFilter.Create(null, null, null).Value;
        filter.Should().Be(new TimelineFilter(null, null, null));
    }

    [Fact]
    public void Offsets_are_converted_to_utc()
    {
        var filter = TimelineFilter.Create(Noon, Noon.AddHours(1), "purchase").Value;

        filter.FromUtc.Should().Be(new DateTime(2026, 9, 21, 5, 0, 0, DateTimeKind.Utc));
        filter.ToUtc.Should().Be(new DateTime(2026, 9, 21, 6, 0, 0, DateTimeKind.Utc));
        filter.Name.Should().Be("purchase");
    }

    [Theory]
    [InlineData("bad name!")]
    [InlineData("purchase\n")]
    [InlineData("")]
    public void Name_must_match_the_event_name_rule(string name)
    {
        var result = TimelineFilter.Create(null, null, name);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("profiles.invalid_filter");
        result.Error.Fields.Should().ContainKey("name");
    }

    [Fact]
    public void From_must_be_before_to()
    {
        var result = TimelineFilter.Create(Noon, Noon, null);

        result.IsFailure.Should().BeTrue();
        result.Error!.Fields.Should().ContainKey("from");
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test tests/Profiles.UnitTests --filter "FullyQualifiedName~Queries"`
Expected: build error — namespace `Crm.Profiles.Application.Queries` does not exist.

- [ ] **Step 3: Implement**

`ProfilesErrors.cs`:

```csharp
using Crm.Kernel;

namespace Crm.Profiles.Application.Queries;

public static class ProfilesErrors
{
    public const string NotFoundCode = "profile.not_found";
    public const string InvalidQueryCode = "profiles.invalid_query";
    public const string InvalidFilterCode = "profiles.invalid_filter";

    /// <summary>One answer for a missing id, another tenant's id and a merge loser: nothing leaks (ruling F8).</summary>
    public static readonly Error NotFound = Error.NotFound(NotFoundCode, "Profile not found.");

    public static Error InvalidQuery(string message)
        => Error.Validation(InvalidQueryCode, $"q {message}", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["q"] = [message] });

    public static Error InvalidFilter(string field, string message)
        => Error.Validation(InvalidFilterCode, $"{field} {message}", new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}
```

`ProfileQueryModels.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Crm.Kernel;

namespace Crm.Profiles.Application.Queries;

/// <summary>Search row: no attributes — that document is uncapped and belongs to the detail endpoint (ruling F7).</summary>
public sealed record ProfileListItem(Guid Id, string? ExternalId, string? Email, string? Phone, DateTime LastSeenAt, long EventCount);

public sealed record ProfileListResponse(IReadOnlyList<ProfileListItem> Items, string? NextCursor);

public sealed record ProfileResponse(Guid Id, string? ExternalId, string? Email, string? Phone, JsonElement Attributes, DateTime FirstSeenAt, DateTime LastSeenAt, long EventCount);

public sealed record TimelineEvent(Guid Id, string Name, JsonElement Properties, DateTime OccurredAt, DateTime ReceivedAt);

public sealed record TimelineResponse(IReadOnlyList<TimelineEvent> Items, string? NextCursor);

/// <summary>`from` inclusive, `to` exclusive, both UTC; `name` is an exact event name (ruling F9).</summary>
public sealed partial record TimelineFilter(DateTime? FromUtc, DateTime? ToUtc, string? Name)
{
    [GeneratedRegex(@"^[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EventName();

    public static Result<TimelineFilter> Create(DateTimeOffset? from, DateTimeOffset? to, string? name)
    {
        if (name is not null && !EventName().IsMatch(name))
        {
            return ProfilesErrors.InvalidFilter("name", "must match [A-Za-z0-9_]{1,64}");
        }

        if (from is not null && to is not null && from.Value >= to.Value)
        {
            return ProfilesErrors.InvalidFilter("from", "must be earlier than to");
        }

        return new TimelineFilter(from?.UtcDateTime, to?.UtcDateTime, name);
    }
}
```

`ProfileSearchTerm.cs`:

```csharp
using System.Text;
using Crm.Kernel;

namespace Crm.Profiles.Application.Queries;

/// <summary>
/// `q` of GET /v1/profiles, ready for SQL LIKE (ruling F6): a prefix over ExternalId and Email, and — when the text
/// is shaped like the start of an E.164 number — over Phone. IdentityKey.Phone/Email cannot be reused: they reject
/// prefixes. Case-insensitivity is the column collation's job.
/// </summary>
public sealed class ProfileSearchTerm
{
    public const int MaxLength = 256;
    public const string LikeEscape = "\\";

    private static readonly ProfileSearchTerm Empty = new(string.Empty, null);

    private ProfileSearchTerm(string textPattern, string? phonePattern)
    {
        TextPattern = textPattern;
        PhonePattern = phonePattern;
    }

    public bool IsEmpty => TextPattern.Length == 0;

    public string TextPattern { get; }

    public string? PhonePattern { get; }

    public static Result<ProfileSearchTerm> Parse(string? q)
    {
        var text = q?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Empty;
        }

        if (text.Length > MaxLength)
        {
            return ProfilesErrors.InvalidQuery($"must be at most {MaxLength} characters");
        }

        return new ProfileSearchTerm(Escape(text) + "%", PhonePrefix(text));
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '%' or '_' or '[')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>"+62 812-3" → "+628123%"; null when anything but digits, one leading '+' and the separators " -.()" is present.</summary>
    private static string? PhonePrefix(string text)
    {
        var digits = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= '0' and <= '9')
            {
                digits.Append(c);
            }
            else if (c == '+' && i == 0)
            {
                // the one allowed '+'
            }
            else if (c is not (' ' or '-' or '.' or '(' or ')'))
            {
                return null;
            }
        }

        return digits.Length is >= 1 and <= 15 ? $"+{digits}%" : null;
    }
}
```

`IProfileQueries.cs`:

```csharp
using Crm.Kernel;

namespace Crm.Profiles.Application.Queries;

/// <summary>Read side of the module. Every method excludes merge losers and is tenant-scoped by the DbContext filter.</summary>
public interface IProfileQueries
{
    Task<Result<ProfileListResponse>> SearchAsync(string? q, int? limit, string? cursor, CancellationToken cancellationToken);

    Task<Result<ProfileResponse>> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<TimelineResponse>> TimelineAsync(Guid id, TimelineFilter filter, int? limit, string? cursor, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Run the tests**

Run: `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test tests/Profiles.UnitTests` then `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test tests/Crm.ArchTests`
Expected: all pass (81 existing + new; arch 16).

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Profiles/Profiles.Application/Queries tests/Profiles.UnitTests/Queries
git commit -m "feat(profiles): query contracts, search-term normalizer and the profiles error catalogue"
```

---
### Task 3: EF Core queries, the three endpoints, host wiring, OpenAPI, integration tests

**Files:**
- Create: `src/Modules/Profiles/Profiles.Infrastructure/Queries/EfProfileQueries.cs`, `src/Modules/Profiles/Profiles.Infrastructure/Endpoints/ProfileEndpoints.cs`, `src/Modules/Profiles/Profiles.Infrastructure/Endpoints/ProfilesEndpointExtensions.cs`
- Modify: `src/Modules/Profiles/Profiles.Infrastructure/ProfilesModule.cs` (register `IProfileQueries`), `src/Modules/Tenancy/Tenancy.Infrastructure/Auth/AuthSchemes.cs` (`EndpointTags.Profiles`), `src/Hosts/Crm.Api/Program.cs` (secured group), `tests/Crm.Api.UnitTests/OpenApiDocumentTests.cs` (two tables), `docs/api/openapi.json` (regenerated), `docs/api/README.md` (§Pagination, §Profiles), `docs/test/integration.md` (IT-007, IT-011, IT-012, IT-030 bullets)
- Test: `tests/Crm.IntegrationTests/Profiles/ProfilesClient.cs`, `ProfileReadTests.cs`, `SearchTests.cs`, `TimelineTests.cs`; edits to `tests/Crm.IntegrationTests/Profiles/MergeTests.cs` (IT-007) and `tests/Crm.IntegrationTests/Tenancy/IsolationTests.cs` (IT-030)

**Interfaces:**
- Consumes: Task 1 (`KeysetCursor`, `PageLimit`, `PagingErrors`), Task 2 (everything in `Crm.Profiles.Application.Queries`), `ProfilesDbContext` (`Profiles`, `Events`; tenant filter applied, merge filter **not**), `Profile` (`ExternalId`, `Email`, `Phone`, `Attributes` JSON text, `FirstSeenAt`, `LastSeenAt`, `EventCount`, `MergedIntoId`), `EventRecord` (`ProfileId`, `Name`, `Properties` JSON text, `OccurredAt`, `ReceivedAt`), host constants `AuthPolicies.AdminOrViewer`, test fixtures `CrmStackFixture`, `CrmApiFactory`, `TenancySeed`, `Eventually`, `ProfilesDb`, `IngestClient.PostWithKeyAsync`.
- Produces: `ProfilesEndpointExtensions.MapProfilesEndpoints(this RouteGroupBuilder secured)`; operation names `SearchProfiles`, `GetProfile`, `GetProfileTimeline`; `EndpointTags.Profiles = "Profiles"`; test helper `ProfilesClient.GetAsync(this HttpClient, string path, string bearer)` and `ProfilesClient.ReadAsync<T>(this HttpClient, string path, string bearer)`.

- [ ] **Step 1: Test helper and the failing integration tests**

`tests/Crm.IntegrationTests/Profiles/ProfilesClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Crm.IntegrationTests.Profiles;

internal static class ProfilesClient
{
    public static Task<HttpResponseMessage> GetAsync(this HttpClient client, string path, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request);
    }

    public static async Task<T> ReadAsync<T>(this HttpClient client, string path, string bearer)
    {
        using var response = await client.GetAsync(path, bearer);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
```

`tests/Crm.IntegrationTests/Profiles/SearchTests.cs` (IT-011). Profiles are created through the pipeline: one `identify` per profile with `email`/`phone` traits (promotion, ruling E6), then one `track` each, spaced so `lastSeenAt` differs:

```csharp
using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class SearchTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;
    private SeededTenant _tenant = null!;
    private string _viewer = null!;

    public async Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        _tenant = await TenancySeed.CreateTenantAsync(_factory);
        _viewer = TenancySeed.AccessToken(_factory, _tenant.ViewerUserId, _tenant.TenantId, Roles.Viewer);
        var key = await TenancySeed.AddApiKeyAsync(_factory, _tenant);

        // Three profiles, identified in this order so lastSeenAt ascends: alice, ALI-9, the phone owner.
        await IdentifyAsync(key.Plaintext, "u-alice", new { email = "alice@x.io" });
        await IdentifyAsync(key.Plaintext, "ALI-9", new { plan = "pro" });
        await IdentifyAsync(key.Plaintext, "u-phone", new { phone = "+62 812-3456-789" });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT011_q_matches_email_external_id_and_phone_by_prefix_case_insensitively()
    {
        var lower = await SearchAsync("ali");
        lower.Items.Select(i => i.ExternalId).Should().Equal("ALI-9", "u-alice"); // lastSeenAt desc

        var upper = await SearchAsync("ALI");
        upper.Items.Select(i => i.ExternalId).Should().Equal("ALI-9", "u-alice");

        var phone = await SearchAsync(Uri.EscapeDataString("+62812"));
        phone.Items.Should().ContainSingle().Which.Phone.Should().Be("+628123456789");

        var lostPlus = await SearchAsync("+62812"); // '+' decodes to a space
        lostPlus.Items.Should().ContainSingle().Which.ExternalId.Should().Be("u-phone");

        (await SearchAsync("zzz")).Items.Should().BeEmpty();
        (await SearchAsync("zzz")).NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task IT011_blank_q_lists_every_live_profile_newest_first_and_pages_without_overlap()
    {
        var first = await _client.ReadAsync<ProfileListResponse>("/v1/profiles?limit=2", _viewer);
        first.Items.Select(i => i.ExternalId).Should().Equal("u-phone", "ALI-9");
        first.NextCursor.Should().NotBeNull();

        var second = await _client.ReadAsync<ProfileListResponse>($"/v1/profiles?limit=2&cursor={first.NextCursor}", _viewer);
        second.Items.Select(i => i.ExternalId).Should().Equal("u-alice");
        second.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task IT011_like_metacharacters_in_q_match_literally()
        => (await SearchAsync(Uri.EscapeDataString("%"))).Items.Should().BeEmpty();

    [Theory]
    [InlineData("/v1/profiles?limit=0", "pagination.invalid_limit")]
    [InlineData("/v1/profiles?limit=201", "pagination.invalid_limit")]
    [InlineData("/v1/profiles?cursor=garbage", "pagination.invalid_cursor")]
    public async Task IT011_bad_paging_arguments_are_400_problem_json(string path, string code)
    {
        using var response = await _client.GetAsync(path, _viewer);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task IT011_a_timeline_cursor_is_rejected_by_search()
    {
        var foreign = new Crm.Kernel.Paging.KeysetCursor(DateTime.UtcNow, Guid.NewGuid()).Encode("events");
        using var response = await _client.GetAsync($"/v1/profiles?cursor={foreign}", _viewer);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private Task<ProfileListResponse> SearchAsync(string encodedQ)
        => _client.ReadAsync<ProfileListResponse>($"/v1/profiles?q={encodedQ}", _viewer);

    private async Task IdentifyAsync(string apiKey, string userId, object traits)
    {
        using var response = await _client.PostWithKeyAsync("/v1/identify", apiKey, new { userId, traits });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, _tenant.TenantId, "external_id", userId),
            $"profile {userId} exists");
        await Task.Delay(20); // lastSeenAt is datetime2(3): keep the three profiles on distinct milliseconds
    }
}
```

(`Roles` lives where `AuthTests.cs` imports it from; use the same `using`.)

`tests/Crm.IntegrationTests/Profiles/TimelineTests.cs` (IT-012): 120 events in one `/v1/batch`, explicit timestamps one second apart counting back from "now − 1 min", every third one named `purchase` (40), and events 10/11 and 60/61 sharing a timestamp so the `Id` tiebreak is exercised across a page boundary candidate:

```csharp
using System.Net;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class TimelineTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;
    private SeededTenant _tenant = null!;
    private string _viewer = null!;
    private Guid _profileId;
    private DateTime _newest;

    public async Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        _tenant = await TenancySeed.CreateTenantAsync(_factory);
        _viewer = TenancySeed.AccessToken(_factory, _tenant.ViewerUserId, _tenant.TenantId, Roles.Viewer);
        var key = await TenancySeed.AddApiKeyAsync(_factory, _tenant);

        var now = DateTime.UtcNow;
        _newest = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc).AddMinutes(-1);
        var events = Enumerable.Range(0, 120).Select(i => new
        {
            type = "track",
            userId = "u-timeline",
            @event = i % 3 == 0 ? "purchase" : "app_open",
            timestamp = _newest.AddSeconds(-(i is 11 or 61 ? i - 1 : i)).ToString("O"),
            properties = new { n = i },
        }).ToArray();

        using var response = await _client.PostWithKeyAsync("/v1/batch", key.Plaintext, new { events });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var profile = await Eventually.GetAsync(
            async () => await ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, _tenant.TenantId, "external_id", "u-timeline") is { EventCount: 120 } p ? p : null,
            "all 120 events are applied");
        _profileId = profile.Id;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT012_pages_are_50_50_20_newest_first_without_overlap()
    {
        var page1 = await PageAsync("limit=50");
        var page2 = await PageAsync($"limit=50&cursor={page1.NextCursor}");
        var page3 = await PageAsync($"limit=50&cursor={page2.NextCursor}");

        new[] { page1.Items.Count, page2.Items.Count, page3.Items.Count }.Should().Equal(50, 50, 20);
        page1.NextCursor.Should().NotBeNull();
        page2.NextCursor.Should().NotBeNull();
        page3.NextCursor.Should().BeNull();

        var all = page1.Items.Concat(page2.Items).Concat(page3.Items).ToList();
        all.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        all.Select(e => e.OccurredAt).Should().BeInDescendingOrder();
        all[0].OccurredAt.Should().Be(_newest);
        all[0].OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
        all[0].Properties.GetProperty("n").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task IT012_default_limit_is_50()
        => (await PageAsync(string.Empty)).Items.Should().HaveCount(50);

    [Fact]
    public async Task IT012_name_filter_returns_the_40_purchases()
    {
        var purchases = await PageAsync("name=purchase&limit=200");

        purchases.Items.Should().HaveCount(40).And.OnlyContain(e => e.Name == "purchase");
        purchases.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task IT012_from_is_inclusive_and_to_is_exclusive()
    {
        var from = Uri.EscapeDataString(_newest.AddSeconds(-5).ToString("O"));
        var to = Uri.EscapeDataString(_newest.AddSeconds(-2).ToString("O"));

        var window = await PageAsync($"from={from}&to={to}");

        window.Items.Select(e => e.Properties.GetProperty("n").GetInt32()).Should().Equal(3, 4, 5);
    }

    [Theory]
    [InlineData("name=bad%20name!")]
    [InlineData("from=2026-09-21T00:00:00Z&to=2026-09-20T00:00:00Z")]
    [InlineData("limit=500")]
    [InlineData("cursor=garbage")]
    public async Task IT012_bad_arguments_are_400(string query)
    {
        using var response = await _client.GetAsync($"/v1/profiles/{_profileId}/events?{query}", _viewer);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task IT012_timeline_of_an_unknown_profile_is_404()
    {
        using var response = await _client.GetAsync($"/v1/profiles/{Guid.NewGuid()}/events", _viewer);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private Task<TimelineResponse> PageAsync(string query)
        => _client.ReadAsync<TimelineResponse>($"/v1/profiles/{_profileId}/events?{query}", _viewer);
}
```

`tests/Crm.IntegrationTests/Profiles/ProfileReadTests.cs` — detail shape, roles, 404:

```csharp
using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure.Auth;
using FluentAssertions;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ProfileReadTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;
    private SeededTenant _tenant = null!;
    private SeededApiKey _key = null!;
    private Guid _profileId;

    public async Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        _tenant = await TenancySeed.CreateTenantAsync(_factory);
        _key = await TenancySeed.AddApiKeyAsync(_factory, _tenant);

        using var response = await _client.PostWithKeyAsync("/v1/identify", _key.Plaintext,
            new { userId = "u-read", traits = new { email = "Read.Me@X.io", plan = "pro", age = 31 } });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _profileId = (await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, _tenant.TenantId, "external_id", "u-read"),
            "profile u-read exists")).Id;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task FR11_detail_returns_core_fields_and_attributes_as_an_object()
    {
        var viewer = TenancySeed.AccessToken(_factory, _tenant.ViewerUserId, _tenant.TenantId, Roles.Viewer);

        var profile = await _client.ReadAsync<ProfileResponse>($"/v1/profiles/{_profileId}", viewer);

        profile.Id.Should().Be(_profileId);
        profile.ExternalId.Should().Be("u-read");
        profile.Email.Should().Be("read.me@x.io");
        profile.Attributes.ValueKind.Should().Be(JsonValueKind.Object);
        profile.Attributes.GetProperty("plan").GetString().Should().Be("pro");
        profile.Attributes.GetProperty("age").GetInt32().Should().Be(31);
        profile.FirstSeenAt.Kind.Should().Be(DateTimeKind.Utc);
        profile.LastSeenAt.Should().BeOnOrAfter(profile.FirstSeenAt);
    }

    [Fact]
    public async Task FR11_detail_json_has_exactly_the_documented_members_and_utc_timestamps()
    {
        var admin = TenancySeed.AccessToken(_factory, _tenant.AdminUserId, _tenant.TenantId, Roles.Admin);
        using var response = await _client.GetAsync($"/v1/profiles/{_profileId}", admin);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "externalId", "email", "phone", "attributes", "firstSeenAt", "lastSeenAt", "eventCount");
        json.RootElement.GetProperty("lastSeenAt").GetString().Should().EndWith("Z");
    }

    [Fact]
    public async Task IT033_api_key_cannot_read_profiles_and_missing_credentials_are_401()
    {
        using var withKey = new HttpRequestMessage(HttpMethod.Get, "/v1/profiles");
        withKey.Headers.Add(AuthSchemes.ApiKeyHeader, _key.Plaintext);
        using var forbidden = await _client.SendAsync(withKey);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var anonymous = await _client.GetAsync("/v1/profiles");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FR11_unknown_id_is_404_profile_not_found()
    {
        var viewer = TenancySeed.AccessToken(_factory, _tenant.ViewerUserId, _tenant.TenantId, Roles.Viewer);
        using var response = await _client.GetAsync($"/v1/profiles/{Guid.NewGuid()}", viewer);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("code").GetString().Should().Be("profile.not_found");
    }
}
```

IT-007 — in `tests/Crm.IntegrationTests/Profiles/MergeTests.cs`, at the end of `IT007_identify_merges_anonymous_into_known_profile`, after the existing assertions on winner and loser rows (the test already holds both ids; `loser`/`winner` below stand for whatever the local variables are called):

```csharp
        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);
        using var merged = await _client.GetAsync($"/v1/profiles/{loser.Id}", viewer);
        merged.StatusCode.Should().Be(HttpStatusCode.NotFound, "a merge loser is excluded from every read (FR-09)");
        using var mergedTimeline = await _client.GetAsync($"/v1/profiles/{loser.Id}/events", viewer);
        mergedTimeline.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.ReadAsync<ProfileResponse>($"/v1/profiles/{winner.Id}", viewer)).EventCount.Should().Be(winner.EventCount);
```

IT-030 — new test in `tests/Crm.IntegrationTests/Tenancy/IsolationTests.cs` (the class already has `_factory`, `_client`, `stack`; add the usings it needs):

```csharp
    [Fact]
    public async Task IT030_another_tenants_profile_reads_are_404()
    {
        var a = await TenancySeed.CreateTenantAsync(_factory);
        var b = await TenancySeed.CreateTenantAsync(_factory);
        var keyB = await TenancySeed.AddApiKeyAsync(_factory, b);
        using (var accepted = await _client.PostWithKeyAsync("/v1/track", keyB.Plaintext, new { userId = "u-of-b", @event = "app_open" }))
        {
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var profileOfB = await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, b.TenantId, "external_id", "u-of-b"),
            "tenant B's profile exists");
        var tokenOfA = TenancySeed.AccessToken(_factory, a.AdminUserId, a.TenantId, Roles.Admin);
        var tokenOfB = TenancySeed.AccessToken(_factory, b.AdminUserId, b.TenantId, Roles.Admin);

        (await _client.GetAsync($"/v1/profiles/{profileOfB.Id}", tokenOfA)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/v1/profiles/{profileOfB.Id}/events", tokenOfA)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=u-of-b", tokenOfA)).Items.Should().BeEmpty();
        (await _client.GetAsync($"/v1/profiles/{profileOfB.Id}", tokenOfB)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
```

- [ ] **Step 2: Run one of them and watch it fail**

Run (environment prefix from Global Constraints): `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test tests/Crm.IntegrationTests --filter "FullyQualifiedName~ProfileReadTests"`
Expected: 4 failures with `404` where `200`/`403` are expected (no route yet) — `FR11_unknown_id…` fails on the missing `code`.

- [ ] **Step 3: Implement the queries**

`src/Modules/Profiles/Profiles.Infrastructure/Queries/EfProfileQueries.cs`:

```csharp
using System.Text.Json;
using Crm.Kernel;
using Crm.Kernel.Paging;
using Crm.Profiles.Application.Queries;
using Microsoft.EntityFrameworkCore;

namespace Crm.Profiles.Infrastructure.Queries;

/// <summary>
/// Read side over ProfilesDbContext. The tenant predicate is the context's global filter; "MergedIntoId is null" is
/// added here on every profile read (FR-09). Lists are keyset-paged: one row more than the page is fetched to learn
/// whether a next page exists, and the cursor is the last returned row's (sort timestamp, id) (rulings F2, F4).
/// </summary>
internal sealed class EfProfileQueries(ProfilesDbContext db) : IProfileQueries
{
    private const string SearchScope = "profiles";
    private const string TimelineScope = "events";

    public async Task<Result<ProfileListResponse>> SearchAsync(string? q, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var size = PageLimit.Resolve(limit);
        if (size.IsFailure)
        {
            return size.Error!;
        }

        var term = ProfileSearchTerm.Parse(q);
        if (term.IsFailure)
        {
            return term.Error!;
        }

        var query = db.Profiles.AsNoTracking().Where(p => p.MergedIntoId == null);

        if (!term.Value.IsEmpty)
        {
            var text = term.Value.TextPattern;
            var phone = term.Value.PhonePattern;
            query = phone is null
                ? query.Where(p => EF.Functions.Like(p.ExternalId!, text, ProfileSearchTerm.LikeEscape)
                    || EF.Functions.Like(p.Email!, text, ProfileSearchTerm.LikeEscape))
                : query.Where(p => EF.Functions.Like(p.ExternalId!, text, ProfileSearchTerm.LikeEscape)
                    || EF.Functions.Like(p.Email!, text, ProfileSearchTerm.LikeEscape)
                    || EF.Functions.Like(p.Phone!, phone, ProfileSearchTerm.LikeEscape));
        }

        if (cursor is not null)
        {
            if (!KeysetCursor.TryDecode(SearchScope, cursor, out var after))
            {
                return PagingErrors.InvalidCursor;
            }

            var at = after.Timestamp;
            var id = after.Id;
            query = query.Where(p => p.LastSeenAt < at || (p.LastSeenAt == at && p.Id.CompareTo(id) < 0));
        }

        var rows = await query
            .OrderByDescending(p => p.LastSeenAt).ThenByDescending(p => p.Id)
            .Take(size.Value + 1)
            .Select(p => new ProfileListItem(p.Id, p.ExternalId, p.Email, p.Phone, p.LastSeenAt, p.EventCount))
            .ToListAsync(cancellationToken);

        var page = rows.Take(size.Value).Select(r => r with { LastSeenAt = Utc(r.LastSeenAt) }).ToList();
        var next = rows.Count > size.Value ? new KeysetCursor(page[^1].LastSeenAt, page[^1].Id).Encode(SearchScope) : null;
        return new ProfileListResponse(page, next);
    }

    public async Task<Result<ProfileResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Profiles.AsNoTracking()
            .Where(p => p.Id == id && p.MergedIntoId == null)
            .Select(p => new { p.Id, p.ExternalId, p.Email, p.Phone, p.Attributes, p.FirstSeenAt, p.LastSeenAt, p.EventCount })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return ProfilesErrors.NotFound;
        }

        return new ProfileResponse(row.Id, row.ExternalId, row.Email, row.Phone, ToElement(row.Attributes), Utc(row.FirstSeenAt), Utc(row.LastSeenAt), row.EventCount);
    }

    public async Task<Result<TimelineResponse>> TimelineAsync(Guid id, TimelineFilter filter, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var size = PageLimit.Resolve(limit);
        if (size.IsFailure)
        {
            return size.Error!;
        }

        KeysetCursor? after = null;
        if (cursor is not null)
        {
            if (!KeysetCursor.TryDecode(TimelineScope, cursor, out var decoded))
            {
                return PagingErrors.InvalidCursor;
            }

            after = decoded;
        }

        if (!await db.Profiles.AsNoTracking().AnyAsync(p => p.Id == id && p.MergedIntoId == null, cancellationToken))
        {
            return ProfilesErrors.NotFound;
        }

        var query = db.Events.AsNoTracking().Where(e => e.ProfileId == id);

        if (filter.Name is { } name)
        {
            query = query.Where(e => e.Name == name);
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(e => e.OccurredAt >= from);
        }

        if (filter.ToUtc is { } to)
        {
            query = query.Where(e => e.OccurredAt < to);
        }

        if (after is { } position)
        {
            var at = position.Timestamp;
            var lastId = position.Id;
            query = query.Where(e => e.OccurredAt < at || (e.OccurredAt == at && e.Id.CompareTo(lastId) < 0));
        }

        var rows = await query
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(size.Value + 1)
            .Select(e => new { e.Id, e.Name, e.Properties, e.OccurredAt, e.ReceivedAt })
            .ToListAsync(cancellationToken);

        var page = rows.Take(size.Value)
            .Select(e => new TimelineEvent(e.Id, e.Name, ToElement(e.Properties), Utc(e.OccurredAt), Utc(e.ReceivedAt)))
            .ToList();
        var next = rows.Count > size.Value ? new KeysetCursor(page[^1].OccurredAt, page[^1].Id).Encode(TimelineScope) : null;
        return new TimelineResponse(page, next);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>The columns carry an ISJSON check constraint, so the text always parses; Clone detaches it from the document.</summary>
    private static JsonElement ToElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
```

Ruling F5 applies to the two `CompareTo` predicates: if EF Core refuses to translate them, replace only those `Where` clauses by starting the query from `db.Profiles.FromSqlInterpolated($"SELECT * FROM profiles.Profiles WHERE LastSeenAt < {at} OR (LastSeenAt = {at} AND Id < {id})")` (and the `profiles.Events` equivalent) and keep everything else in LINQ.

Register it in `ProfilesModule.AddProfilesModule`, next to the other scoped services:

```csharp
        services.AddScoped<IProfileQueries, EfProfileQueries>();
```

- [ ] **Step 4: Implement the endpoints**

`src/Modules/Profiles/Profiles.Infrastructure/Endpoints/ProfileEndpoints.cs`:

```csharp
using Crm.Profiles.Application.Queries;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Crm.Profiles.Infrastructure.Endpoints;

internal static class ProfileEndpoints
{
    public static void Map(RouteGroupBuilder secured)
    {
        var profiles = secured.MapGroup("/profiles");

        profiles.MapGet("/", SearchAsync)
            .WithName("SearchProfiles")
            .WithSummary("Prefix search over external id, email and phone; all live profiles when q is absent. Sorted by lastSeenAt, newest first.")
            .Produces<ProfileListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        profiles.MapGet("/{id:guid}", GetAsync)
            .WithName("GetProfile")
            .Produces<ProfileResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        profiles.MapGet("/{id:guid}/events", TimelineAsync)
            .WithName("GetProfileTimeline")
            .WithSummary("Events of one profile, newest occurredAt first; from is inclusive, to is exclusive.")
            .Produces<TimelineResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<object> SearchAsync(
        [FromQuery(Name = "q")] string? q, [FromQuery] int? limit, [FromQuery] string? cursor,
        IProfileQueries queries, CancellationToken cancellationToken)
        => (await queries.SearchAsync(q, limit, cursor, cancellationToken))
            .Match<object>(page => TypedResults.Ok(page), error => error);

    private static async Task<object> GetAsync(Guid id, IProfileQueries queries, CancellationToken cancellationToken)
        => (await queries.GetAsync(id, cancellationToken))
            .Match<object>(profile => TypedResults.Ok(profile), error => error);

    private static async Task<object> TimelineAsync(
        Guid id,
        [FromQuery(Name = "from")] DateTimeOffset? fromTime, [FromQuery(Name = "to")] DateTimeOffset? toTime,
        [FromQuery] string? name, [FromQuery] int? limit, [FromQuery] string? cursor,
        IProfileQueries queries, CancellationToken cancellationToken)
    {
        var filter = TimelineFilter.Create(fromTime, toTime, name);
        if (filter.IsFailure)
        {
            return filter.Error!;
        }

        return (await queries.TimelineAsync(id, filter.Value, limit, cursor, cancellationToken))
            .Match<object>(page => TypedResults.Ok(page), error => error);
    }
}
```

`src/Modules/Profiles/Profiles.Infrastructure/Endpoints/ProfilesEndpointExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Routing;

namespace Crm.Profiles.Infrastructure.Endpoints;

public static class ProfilesEndpointExtensions
{
    /// <summary>
    /// Maps GET /profiles, /profiles/{id} and /profiles/{id}/events onto a group the HOST has already secured
    /// (AdminOrViewer policy, Profiles tag): the policy names live in Tenancy, and a module may not reference another module.
    /// </summary>
    public static RouteGroupBuilder MapProfilesEndpoints(this RouteGroupBuilder secured)
    {
        ArgumentNullException.ThrowIfNull(secured);
        ProfileEndpoints.Map(secured);
        return secured;
    }
}
```

If `Profiles.Infrastructure.csproj` has no ASP.NET Core reference yet, add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` the way `Ingestion.Infrastructure.csproj` does.

`AuthSchemes.cs` — add to `EndpointTags`:

```csharp
    public const string Profiles = "Profiles";
```

`Program.cs` — directly after the Ingestion group:

```csharp
        // Same composition-root rule as Ingestion: Profiles may not name Tenancy's policies or tags.
        v1.MapGroup(string.Empty)
            .RequireAuthorization(AuthPolicies.AdminOrViewer)
            .WithTags(EndpointTags.Profiles)
            .MapProfilesEndpoints();
```

- [ ] **Step 5: Contract — unit test tables, exported document**

In `tests/Crm.Api.UnitTests/OpenApiDocumentTests.cs` insert into `Expected`, after `"GET /v1/api-keys"`:

```csharp
        "GET /v1/profiles",
        "GET /v1/profiles/{id}",
        "GET /v1/profiles/{id}/events",
```

and into `ExpectedOperations`:

```csharp
        new("GET", "/v1/profiles", ["200", "400", "401", "403"], Security: "bearer"),
        new("GET", "/v1/profiles/{id}", ["200", "401", "403", "404"], Security: "bearer"),
        new("GET", "/v1/profiles/{id}/events", ["200", "400", "401", "403", "404"], Security: "bearer"),
```

Run `DOTNET_SYSTEM_NET_DISABLEIPV6=1 make openapi`, then `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test tests/Crm.Api.UnitTests` → green. Open `docs/api/openapi.json` and check: the three operations carry tag `Profiles`; `ProfileResponse.attributes` and `TimelineEvent.properties` exist as properties (an empty property schema `{}` is acceptable — `CT001_no_response_schema_is_empty` inspects the response schema itself).

- [ ] **Step 6: Run the integration tests of this task, then the whole suite**

Run: `… dotnet test tests/Crm.IntegrationTests --filter "FullyQualifiedName~ProfileReadTests|FullyQualifiedName~SearchTests|FullyQualifiedName~TimelineTests|FullyQualifiedName~MergeTests|FullyQualifiedName~IsolationTests"` → green. Then the full suite once, foreground, output to a log file, 30-minute tool timeout → green, 0 skipped.

- [ ] **Step 7: Docs that describe what just changed**

`docs/api/README.md`:
- §Pagination — append: "`limit` outside 1…200 is `400 pagination.invalid_limit`; a `cursor` that this endpoint did not issue (malformed, or issued by another endpoint) is `400 pagination.invalid_cursor`. A cursor is a position in the sort order, not a snapshot: rows that change their sort key between two pages can appear twice or not at all."
- §Profiles — replace the table's descriptions and the shape block so they say: search matches `q` as a case-insensitive **prefix** of `externalId`, `email`, or — when `q` is a `+` and/or digits with optional separators — `phone`; `q` absent lists every profile; `q` ≤ 256 characters (`400 profiles.invalid_query`); anonymous ids are not searchable; search items are `{ "id", "externalId", "email", "phone", "lastSeenAt", "eventCount" }`; the detail shape stays as documented; the timeline is ordered by `occurredAt` (then `id`) descending, so an event sent with an old `timestamp` appears at that position; `name` must match `[A-Za-z0-9_]{1,64}`, `from` is inclusive, `to` exclusive, both ISO 8601 with offset (`400 profiles.invalid_filter`); `404 profile.not_found` for an unknown id, another tenant's id, and a profile that was merged into another one — on both `/{id}` and `/{id}/events`; no rate limit applies to these endpoints; all timestamps are UTC (`Z`).
- Errors table of codes (where `auth.invalid_credentials` etc. are listed): add `pagination.invalid_limit`, `pagination.invalid_cursor`, `profiles.invalid_query`, `profiles.invalid_filter`, `profile.not_found`.

`docs/test/integration.md`: IT-011 and IT-012 bullets → `- Implemented: tests/Crm.IntegrationTests/Profiles/SearchTests.cs` / `…/TimelineTests.cs`; IT-007: replace the "Plan E note" with "Plan F: `GET /v1/profiles/{P2}` and its timeline return 404 (`MergeTests.cs`)."; IT-030: add "Plan F note: the profile reads are asserted by `IT030_another_tenants_profile_reads_are_404`; the segment reads arrive with the Segmentation plan."

Run `make docs-check` → 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src tests docs/api docs/test/integration.md
git commit -m "feat(profiles): GET /v1/profiles search, detail and timeline with keyset cursors (FR-11, IT-007, IT-011, IT-012, IT-030)"
```

---

### Task 4: Track round trip through the API (IT-001) and one trace across the pipeline (IT-040)

**Files:**
- Create: `tests/Crm.IntegrationTests/Ingestion/TrackRoundTripTests.cs`, `tests/Crm.IntegrationTests/Ops/ObservabilityTests.cs`
- Modify: `docs/test/integration.md` (IT-001, IT-040 bullets)

**Interfaces:**
- Consumes: Task 3's endpoints and `ProfilesClient`; `WorkerMetrics`-style access to the Worker: `stack.Worker.CreateClient()` for `/metrics` (see `DeadLetterTests.cs` for how the existing tests obtain the Worker's `HttpClient`), API `/metrics` through `_client`.
- Produces: nothing later tasks use.

- [ ] **Step 1: IT-001**

`tests/Crm.IntegrationTests/Ingestion/TrackRoundTripTests.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Profiles;
using Crm.Profiles.Application.Queries;
using Crm.Tenancy.Domain;
using FluentAssertions;

namespace Crm.IntegrationTests.Ingestion;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class TrackRoundTripTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task IT001_tracked_event_appears_in_the_profile_timeline_within_2_seconds()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);

        // Warm the path (JIT, connection pools, first Kafka produce) so the measured call is a steady-state one.
        using (var warm = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u-warm", @event = "app_open" }))
        {
            warm.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var sent = Stopwatch.StartNew();
        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase", properties = new { amount = 49.9 } });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();

        TimelineEvent? seen = null;
        while (seen is null && sent.Elapsed < Budget * 5)
        {
            var found = await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=u1", viewer);
            if (found.Items.SingleOrDefault(i => i.ExternalId == "u1") is { } profile)
            {
                var timeline = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{profile.Id}/events", viewer);
                seen = timeline.Items.SingleOrDefault(e => e.Id == eventId);
            }

            if (seen is null)
            {
                await Task.Delay(50);
            }
        }

        seen.Should().NotBeNull("the event must reach the timeline");
        sent.Elapsed.Should().BeLessThan(Budget, "NFR-02: ingest-to-timeline within 2 s locally (3x in CI, remote engine)");
        seen!.Name.Should().Be("purchase");
        seen.Properties.GetProperty("amount").GetDouble().Should().Be(49.9);
    }

    /// <summary>2 s locally; 6 s in CI or against a remote engine (docs/test/integration.md: 3x multiplier).</summary>
    private static TimeSpan Budget
        => Environment.GetEnvironmentVariable("CI") == "true" || Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE") is { Length: > 0 }
            ? TimeSpan.FromSeconds(6)
            : TimeSpan.FromSeconds(2);
}
```

The 50 ms p95 half of IT-001's Then-clause stays where plan D put it (`TrackPipelineTests`/ingest tests); this class owns the GET half.

- [ ] **Step 2: IT-040**

`tests/Crm.IntegrationTests/Ops/ObservabilityTests.cs` — an `ActivityListener` over every source records stopped activities; the test finds the trace id of its own request and classifies spans by kind and tags, not by source name (ruling F16):

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Crm.IntegrationTests.Fixtures;
using Crm.IntegrationTests.Ingestion;
using FluentAssertions;

namespace Crm.IntegrationTests.Ops;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ObservabilityTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private ActivityListener _listener = null!;
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _listener.Dispose();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT040_one_trace_spans_http_kafka_produce_worker_consume_and_sql()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u-trace", @event = "app_open" });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Eventually.GetAsync(
            () => ProfilesDb.FindByIdentityAsync(stack.SqlConnectionString, tenant.TenantId, "external_id", "u-trace"),
            "the Worker applied the event");

        var consume = await Eventually.GetAsync(
            () => Task.FromResult(_stopped.FirstOrDefault(a => a.Kind == ActivityKind.Consumer && TagsMention(a, tenant.TenantId))),
            "the Worker's consume span for this tenant has stopped");

        var trace = _stopped.Where(a => a.TraceId == consume.TraceId).ToList();
        trace.Should().Contain(a => a.Kind == ActivityKind.Server, "the API's HTTP span starts the trace");
        trace.Should().Contain(a => a.Kind == ActivityKind.Producer, "the Kafka produce span carries traceparent");
        trace.Should().Contain(a => a.Kind == ActivityKind.Client && a.Tags.Any(t => t.Key.StartsWith("db.", StringComparison.Ordinal)), "the SQL insert runs under the consume span");
    }

    [Fact]
    public async Task IT040_metrics_expose_ingest_counter_and_consumer_lag()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        using (var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u-metric", @event = "app_open" }))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        var api = await _client.GetStringAsync("/metrics");
        api.Should().MatchRegex(@"crm_ingest_events_total\{[^}]*\} [1-9]");

        using var worker = stack.Worker.CreateClient();
        await Eventually.TrueAsync(
            async () => (await worker.GetStringAsync("/metrics")).Contains("crm_consumer_lag", StringComparison.Ordinal),
            "the Worker reports crm_consumer_lag after its first poll cycle");
    }

    private static bool TagsMention(Activity activity, Guid tenantId)
        => activity.TagObjects.Any(t => t.Value?.ToString() is { } v
            && (v.Contains(tenantId.ToString("N"), StringComparison.OrdinalIgnoreCase) || v.Contains(tenantId.ToString("D"), StringComparison.OrdinalIgnoreCase)));
}
```

If the Worker's consume span carries no tag that contains the tenant id (check `RawEventsConsumerService`'s `StartActivity` call), add the tag there rather than weakening the test: `activity?.SetTag("crm.tenant_id", envelope.TenantId.ToString("N"))` right after the envelope is decoded — a one-line production change that also makes Jaeger searchable by tenant; add `src/Modules/Profiles/Profiles.Infrastructure/Consumers/RawEventsConsumerService.cs` to the commit and say so in the report. The log half of IT-040 ("logs carry `TenantId` and `TraceId`") is asserted where the Serilog enrichers were introduced (foundation plan); do not duplicate it.

If no SQL client span appears (the SqlClient instrumentation is not part of the Worker's tracer in the Testing environment), report it as a finding with the list of `Source.Name` values seen in `trace` — do not delete the assertion.

- [ ] **Step 3: Run**

Run: `… dotnet test tests/Crm.IntegrationTests --filter "FullyQualifiedName~TrackRoundTripTests|FullyQualifiedName~ObservabilityTests"` → green. Repeat the run twice more: both classes share one Worker with the rest of the suite, so a flake shows up as an intermittent failure — report it rather than raising a budget.

- [ ] **Step 4: Docs and commit**

`docs/test/integration.md`: IT-001 → `- Implemented: tests/Crm.IntegrationTests/Ingestion/TrackRoundTripTests.cs` (drop the plan E note's "arrives with plan F" half-sentence); IT-040 → `- Implemented: tests/Crm.IntegrationTests/Ops/ObservabilityTests.cs`. `make docs-check` → 0 errors.

```bash
git add tests/Crm.IntegrationTests docs/test/integration.md src/Modules/Profiles/Profiles.Infrastructure/Consumers
git commit -m "test(integration): track round trip through the read API and one trace across the pipeline (IT-001, IT-040)"
```

---
### Task 5: Deterministic seed dataset generator (pure)

**Files:**
- Create: `src/Modules/Profiles/Profiles.Application/Seeding/SeedPrng.cs`, `SeedModels.cs`, `SeedExpectations.cs`, `SeedDatasetGenerator.cs`
- Test: `tests/Profiles.UnitTests/Seeding/SeedPrngTests.cs`, `tests/Profiles.UnitTests/Seeding/SeedDatasetGeneratorTests.cs`

**Interfaces:**
- Consumes: nothing outside the BCL.
- Produces (namespace `Crm.Profiles.Application.Seeding`):
  - `SeedPrng(ulong seed)` with `ulong NextUInt64()`, `int NextInt(int exclusiveMax)`, `double NextDouble()`
  - `SeedProfile(int Index, string ExternalId, string Email, string? Phone, string? Plan, string? Country, string? City, bool? OptedIn, decimal? Ltv, bool Dormant, bool HasEvents, bool Purchaser)`
  - `SeedEvent(int ProfileIndex, string Name, TimeSpan Age, decimal? Amount)` — `Age` = load time − `OccurredAt`
  - `SeedMerge(int Number, string AnonymousId, int WinnerIndex)`
  - `SeedData(IReadOnlyList<SeedProfile> Profiles, IReadOnlyList<SeedEvent> Events, IReadOnlyList<SeedMerge> Merges)`
  - `SeedExpectations`: `const int Profiles = 5000`, `MergedProfiles = 50`, `Events = 100000`, `const ulong PrngSeed = 20260915`, `IReadOnlyDictionary<string, int> Segments` (the six names and counts), `const string PinnedExternalId = "u-00001"`
  - `SeedDatasetGenerator.Generate()` → `SeedData` (no arguments: everything is fixed)

**Design (ruling F13).** The 5,000 profiles are the expansion of this cell table (active / dormant per plan × country), shuffled with the PRNG; nothing else decides plan, country or dormancy, so the marginals and the joint count behind `seed-pro-id` are exact:

| plan \ country | ID | SG | MY | TH | none | total |
|---|---|---|---|---|---|---|
| `pro` | 200 / 40 | 100 / 22 | 250 / 20 | 170 / 15 | 168 / 15 | 1,000 |
| `free` | 1,008 / 552 | 648 / 355 | 262 / 143 | 171 / 94 | 173 / 94 | 3,500 |
| none | 140 / 60 | 88 / 37 | 52 / 23 | 35 / 15 | 35 / 15 | 500 |
| total | 2,000 | 1,250 | 750 | 500 | 500 | 5,000 (1,500 dormant) |

`seed-pro-id` = pro ∧ (ID ∨ SG) (362) + pro ∧ dormant elsewhere (50) = 412. Dormant = no `app_open` younger than 7 days: dormant profiles only get events at least 8 days old, and every sixth dormant profile (250) gets none at all; every active profile gets one mandatory `app_open` between 1 hour and 3 days old. The first 2,731 profiles (by index) that have events are purchasers: one mandatory `purchase` younger than 25 days (dormant ones: 8…25 days); every other profile's purchases are at least 35 days old. Of the SG profiles (by index) the first 187 get `ltv` ≥ 150, the next 813 `ltv` < 150, the last 250 none; of the others every fifth has none (4,000 present in total). The margins (3 d vs 7 d, 25/35 d vs 30 d) keep all six counts stable for four days after loading.

- [ ] **Step 1: Write the failing tests**

`tests/Profiles.UnitTests/Seeding/SeedPrngTests.cs`:

```csharp
using Crm.Profiles.Application.Seeding;
using FluentAssertions;

namespace Crm.Profiles.UnitTests.Seeding;

public sealed class SeedPrngTests
{
    [Fact]
    public void SplitMix64_reference_vector_for_seed_1234567()
    {
        var prng = new SeedPrng(1234567);
        new[] { prng.NextUInt64(), prng.NextUInt64(), prng.NextUInt64() }
            .Should().Equal(6457827717110365317UL, 3203168211198807973UL, 9817491932198370423UL);
    }

    [Fact]
    public void NextInt_stays_below_the_bound_and_NextDouble_inside_the_unit_interval()
    {
        var prng = new SeedPrng(1);
        for (var i = 0; i < 10_000; i++)
        {
            prng.NextInt(7).Should().BeInRange(0, 6);
            prng.NextDouble().Should().BeInRange(0d, 1d).And.BeLessThan(1d);
        }
    }
}
```

`tests/Profiles.UnitTests/Seeding/SeedDatasetGeneratorTests.cs`:

```csharp
using Crm.Profiles.Application.Seeding;
using FluentAssertions;

namespace Crm.Profiles.UnitTests.Seeding;

public sealed class SeedDatasetGeneratorTests
{
    private static readonly SeedData Data = SeedDatasetGenerator.Generate();
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);
    private static readonly TimeSpan Month = TimeSpan.FromDays(30);

    [Fact]
    public void Two_runs_produce_the_same_dataset()
    {
        var again = SeedDatasetGenerator.Generate();
        again.Profiles.Should().Equal(Data.Profiles);
        again.Events.Should().Equal(Data.Events);
        again.Merges.Should().Equal(Data.Merges);
    }

    [Fact]
    public void Totals_match_the_fixture_document()
    {
        Data.Profiles.Should().HaveCount(5000);
        Data.Events.Should().HaveCount(100_000);
        Data.Merges.Should().HaveCount(50);
        Data.Profiles.Select(p => p.ExternalId).Should().OnlyHaveUniqueItems().And.Contain("u-00001").And.Contain("u-05000");
        Data.Profiles.Select(p => p.Email).Should().OnlyHaveUniqueItems();
        Data.Profiles.Single(p => p.Index == 1).Email.Should().Be("alice.1@example.test");
        Data.Profiles.Where(p => p.Phone is not null).Should().HaveCount(3000).And.OnlyContain(p => p.Phone!.StartsWith("+62812") && p.Phone.Length == 13);
    }

    [Fact]
    public void Attribute_marginals_are_exact()
    {
        Count(p => p.Plan == "pro").Should().Be(1000);
        Count(p => p.Plan == "free").Should().Be(3500);
        Count(p => p.Plan is null).Should().Be(500);
        Count(p => p.Country == "ID").Should().Be(2000);
        Count(p => p.Country == "SG").Should().Be(1250);
        Count(p => p.Country == "MY").Should().Be(750);
        Count(p => p.Country == "TH").Should().Be(500);
        Count(p => p.Country is null).Should().Be(500);
        Count(p => p.City is not null).Should().Be(2500);
        Count(p => p.OptedIn == true).Should().Be(1500);
        Count(p => p.OptedIn == false).Should().Be(1500);
        Count(p => p.Ltv is not null).Should().Be(4000);
    }

    [Fact]
    public void Event_name_mix_is_exact_and_amounts_belong_to_purchases()
    {
        Data.Events.Count(e => e.Name == "app_open").Should().Be(60_000);
        Data.Events.Count(e => e.Name == "purchase").Should().Be(20_000);
        Data.Events.Count(e => e.Name == "add_to_cart").Should().Be(15_000);
        Data.Events.Count(e => e.Name == "logout").Should().Be(5_000);
        Data.Events.Should().OnlyContain(e => (e.Name == "purchase") == (e.Amount != null));
        Data.Events.Where(e => e.Amount is not null).Should().OnlyContain(e => e.Amount >= 5m && e.Amount <= 200m);
        Data.Events.Should().OnlyContain(e => e.Age >= TimeSpan.FromHours(1) && e.Age < TimeSpan.FromDays(90));
    }

    [Fact]
    public void Activity_mix_is_exact()
    {
        var withEvents = Data.Events.Select(e => e.ProfileIndex).ToHashSet();
        Data.Profiles.Count(p => !withEvents.Contains(p.Index)).Should().Be(250, "5 % have no events at all");
        var recent = Data.Events.Where(e => e.Age < Week).Select(e => e.ProfileIndex).ToHashSet();
        Data.Profiles.Count(p => !recent.Contains(p.Index)).Should().Be(1500, "30 % have no event in the last 7 days");
    }

    [Fact]
    public void The_six_documented_segment_counts_hold_at_load_time_and_four_days_later()
    {
        foreach (var drift in new[] { TimeSpan.Zero, TimeSpan.FromDays(4) })
        {
            var appOpenIn7d = Data.Events.Where(e => e.Name == "app_open" && e.Age + drift < Week).Select(e => e.ProfileIndex).ToHashSet();
            var purchaseIn30d = Data.Events.Where(e => e.Name == "purchase" && e.Age + drift < Month).Select(e => e.ProfileIndex).ToHashSet();

            var actual = new Dictionary<string, int>
            {
                ["seed-pro-id"] = Count(p => p.Plan == "pro" && (p.Country is "ID" or "SG" || !appOpenIn7d.Contains(p.Index))),
                ["seed-purchasers"] = Count(p => purchaseIn30d.Contains(p.Index)),
                ["seed-dormant"] = Count(p => !appOpenIn7d.Contains(p.Index)),
                ["seed-no-plan"] = Count(p => p.Plan is null),
                ["seed-not-pro"] = Count(p => p.Plan != "pro"),
                ["seed-high-ltv-sg"] = Count(p => p.Ltv >= 150m && p.Country == "SG"),
            };

            actual.Should().Equal(SeedExpectations.Segments, $"drift {drift.TotalDays} d");
        }

        SeedExpectations.Segments.Should().Equal(new Dictionary<string, int>
        {
            ["seed-pro-id"] = 412, ["seed-purchasers"] = 2731, ["seed-dormant"] = 1500,
            ["seed-no-plan"] = 500, ["seed-not-pro"] = 4000, ["seed-high-ltv-sg"] = 187,
        });
    }

    [Fact]
    public void Pinned_profile_is_an_active_pro_purchaser_in_ID_with_more_than_120_events()
    {
        var alice = Data.Profiles.Single(p => p.ExternalId == SeedExpectations.PinnedExternalId);
        alice.Should().Match<SeedProfile>(p => p.Plan == "pro" && p.Country == "ID" && !p.Dormant && p.Purchaser);
        Data.Events.Count(e => e.ProfileIndex == alice.Index).Should().BeGreaterThan(120);
    }

    [Fact]
    public void Merges_point_at_distinct_existing_winners()
    {
        Data.Merges.Select(m => m.AnonymousId).Should().OnlyHaveUniqueItems();
        Data.Merges.Select(m => m.WinnerIndex).Should().OnlyHaveUniqueItems().And.OnlyContain(i => i >= 1 && i <= 5000);
    }

    private static int Count(Func<SeedProfile, bool> predicate) => Data.Profiles.Count(predicate);
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test tests/Profiles.UnitTests --filter "FullyQualifiedName~Seeding"` → build error (namespace missing).

- [ ] **Step 3: Implement**

`SeedPrng.cs`:

```csharp
namespace Crm.Profiles.Application.Seeding;

/// <summary>SplitMix64: fifteen lines we own, so the dataset never depends on System.Random's algorithm (fixtures.md: seed 20260915).</summary>
public sealed class SeedPrng(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Modulo bias is irrelevant here: the generator's exact counts come from quotas, not from this.</summary>
    public int NextInt(int exclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMax);
        return (int)(NextUInt64() % (ulong)exclusiveMax);
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
}
```

`SeedModels.cs`:

```csharp
namespace Crm.Profiles.Application.Seeding;

public sealed record SeedProfile(
    int Index, string ExternalId, string Email, string? Phone, string? Plan, string? Country, string? City,
    bool? OptedIn, decimal? Ltv, bool Dormant, bool HasEvents, bool Purchaser);

/// <summary>Age = load time minus OccurredAt, so the generator is independent of the clock.</summary>
public sealed record SeedEvent(int ProfileIndex, string Name, TimeSpan Age, decimal? Amount);

/// <summary>One anonymous profile that was identified into <see cref="WinnerIndex"/>: the loader writes the loser row and the re-pointed anonymous identity.</summary>
public sealed record SeedMerge(int Number, string AnonymousId, int WinnerIndex);

public sealed record SeedData(IReadOnlyList<SeedProfile> Profiles, IReadOnlyList<SeedEvent> Events, IReadOnlyList<SeedMerge> Merges);
```

`SeedExpectations.cs`:

```csharp
using System.Collections.ObjectModel;

namespace Crm.Profiles.Application.Seeding;

/// <summary>docs/test/fixtures.md in code: the loader verifies these after loading (ruling F15).</summary>
public static class SeedExpectations
{
    public const ulong PrngSeed = 20260915;
    public const int Profiles = 5000;
    public const int MergedProfiles = 50;
    public const int Events = 100_000;
    public const string PinnedExternalId = "u-00001";

    public static IReadOnlyDictionary<string, int> Segments { get; } = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["seed-pro-id"] = 412,
        ["seed-purchasers"] = 2731,
        ["seed-dormant"] = 1500,
        ["seed-no-plan"] = 500,
        ["seed-not-pro"] = 4000,
        ["seed-high-ltv-sg"] = 187,
    });
}
```

`SeedDatasetGenerator.cs`:

```csharp
namespace Crm.Profiles.Application.Seeding;

/// <summary>
/// The demo dataset of docs/test/fixtures.md. Quota-based: a fixed cell table decides plan, country and dormancy, so the
/// six documented segment counts are exact; the PRNG only shuffles cells over indexes and spreads the filler events
/// (ruling F13). Pure: no clock, no I/O.
/// </summary>
public static class SeedDatasetGenerator
{
    private const int Hour = 3600;
    private const int Day = 86_400;

    private static readonly string[] FirstNames =
    [
        "alice", "bob", "carol", "dave", "erin", "frank", "grace", "heidi", "ivan", "judy",
        "karl", "lena", "mallory", "nina", "oscar", "peggy", "quinn", "rupert", "sybil", "trent",
        "uma", "victor", "wendy", "xavier", "yara", "zane", "adi", "budi", "citra", "dewi",
        "eka", "fajar", "gita", "hadi", "indah", "joko", "kartika", "lukman", "maya", "nanda",
        "omar", "putri", "rani", "sari", "tono", "utami", "vina", "wawan", "yuli", "zahra",
    ];

    private static readonly string[] Cities = ["Jakarta", "Surabaya", "Bandung", "Medan", "Singapore", "Kuala Lumpur", "Penang", "Bangkok", "Chiang Mai", "Denpasar"];

    private static readonly string?[] Countries = ["ID", "SG", "MY", "TH", null];

    /// <summary>Rows: pro, free, no plan. Columns follow <see cref="Countries"/>. Each cell: (active, dormant).</summary>
    private static readonly (string? Plan, (int Active, int Dormant)[] Cells)[] CellTable =
    [
        ("pro", [(200, 40), (100, 22), (250, 20), (170, 15), (168, 15)]),
        ("free", [(1008, 552), (648, 355), (262, 143), (171, 94), (173, 94)]),
        (null, [(140, 60), (88, 37), (52, 23), (35, 15), (35, 15)]),
    ];

    /// <summary>The pinned profile's extra events (ruling F14), taken out of the name quotas below.</summary>
    private static readonly (string Name, int Count)[] PinnedExtras = [("app_open", 72), ("purchase", 24), ("add_to_cart", 18), ("logout", 6)];

    private static readonly (string Name, int Quota)[] NameQuotas = [("app_open", 60_000), ("purchase", 20_000), ("add_to_cart", 15_000), ("logout", 5_000)];

    private const int Purchasers = 2731;
    private const int HighLtvSg = 187;
    private const int LowLtvSg = 813;

    public static SeedData Generate()
    {
        var prng = new SeedPrng(SeedExpectations.PrngSeed);
        var profiles = BuildProfiles(prng);
        var events = BuildEvents(prng, profiles);
        var merges = Enumerable.Range(1, SeedExpectations.MergedProfiles)
            .Select(n => new SeedMerge(n, $"anon-seed-{n:D3}", n * 100))
            .ToList();
        return new SeedData(profiles, events, merges);
    }

    private static List<SeedProfile> BuildProfiles(SeedPrng prng)
    {
        var labels = new List<(string? Plan, string? Country, bool Dormant)>(SeedExpectations.Profiles);
        foreach (var (plan, cells) in CellTable)
        {
            for (var c = 0; c < cells.Length; c++)
            {
                labels.AddRange(Enumerable.Repeat((plan, Countries[c], false), cells[c].Active));
                labels.AddRange(Enumerable.Repeat((plan, Countries[c], true), cells[c].Dormant));
            }
        }

        for (var i = labels.Count - 1; i > 0; i--)
        {
            var j = prng.NextInt(i + 1);
            (labels[i], labels[j]) = (labels[j], labels[i]);
        }

        // Ruling F14: index 1 (alice.1@example.test) is an active pro profile in ID.
        var pin = labels.FindIndex(l => l is ("pro", "ID", false));
        (labels[0], labels[pin]) = (labels[pin], labels[0]);

        var profiles = new List<SeedProfile>(labels.Count);
        int dormantSeen = 0, withEventsSeen = 0, sgSeen = 0, otherSeen = 0;
        for (var index = 1; index <= labels.Count; index++)
        {
            var (plan, country, dormant) = labels[index - 1];
            var hasEvents = !dormant || dormantSeen++ % 6 != 0;
            var purchaser = hasEvents && withEventsSeen++ < Purchasers;

            decimal? ltv;
            if (country == "SG")
            {
                ltv = sgSeen < HighLtvSg ? 150m + Money(850 * Cube(prng.NextDouble()))
                    : sgSeen < HighLtvSg + LowLtvSg ? 1m + Money(148.98 * Square(prng.NextDouble()))
                    : null;
                sgSeen++;
            }
            else
            {
                ltv = otherSeen++ % 5 == 4 ? null : 1m + Money(999 * Cube(prng.NextDouble()));
            }

            profiles.Add(new SeedProfile(
                index,
                $"u-{index:D5}",
                $"{FirstNames[(index - 1) % FirstNames.Length]}.{index}@example.test",
                index % 5 < 3 ? $"+62812{1_000_000 + index:D7}" : null,
                plan,
                country,
                index % 2 == 0 ? Cities[index / 2 % Cities.Length] : null,
                (index % 10) switch { < 3 => true, < 6 => false, _ => null },
                ltv,
                dormant,
                hasEvents,
                purchaser));
        }

        return profiles;
    }

    private static List<SeedEvent> BuildEvents(SeedPrng prng, List<SeedProfile> profiles)
    {
        var events = new List<SeedEvent>(SeedExpectations.Events);
        var remaining = NameQuotas.ToDictionary(q => q.Name, q => q.Quota, StringComparer.Ordinal);
        var withEvents = profiles.Where(p => p.HasEvents).ToList();

        void Add(SeedProfile profile, string name, int minAgeSeconds, int maxAgeSeconds)
        {
            var age = TimeSpan.FromSeconds(minAgeSeconds + prng.NextInt(maxAgeSeconds - minAgeSeconds)) + TimeSpan.FromMilliseconds(prng.NextInt(1000));
            decimal? amount = name == "purchase" ? 5m + Money(195 * prng.NextDouble()) : null;
            events.Add(new SeedEvent(profile.Index, name, age, amount));
            remaining[name]--;
        }

        // Filler events: anywhere in the profile's window, except that a non-purchaser's purchases are older than 35 days.
        void AddFiller(SeedProfile profile, string name)
        {
            var youngest = profile.Dormant ? 8 * Day : Hour;
            if (name == "purchase" && !profile.Purchaser)
            {
                youngest = 35 * Day;
            }

            Add(profile, name, youngest, 89 * Day);
        }

        foreach (var profile in withEvents)
        {
            // Active: one app_open younger than 3 days. Dormant with events: one app_open older than 8 days, so "no events at all" stays at exactly 250.
            Add(profile, "app_open", profile.Dormant ? 8 * Day : Hour, profile.Dormant ? 89 * Day : 3 * Day);
            if (profile.Purchaser)
            {
                Add(profile, "purchase", profile.Dormant ? 8 * Day : Hour, 25 * Day);
            }
        }

        var pinned = profiles[0];
        foreach (var (name, count) in PinnedExtras)
        {
            for (var i = 0; i < count; i++)
            {
                AddFiller(pinned, name);
            }
        }

        foreach (var (name, _) in NameQuotas)
        {
            while (remaining[name] > 0)
            {
                AddFiller(withEvents[prng.NextInt(withEvents.Count)], name);
            }
        }

        return events;
    }

    private static double Square(double u) => u * u;

    private static double Cube(double u) => u * u * u;

    /// <summary>Two decimals, computed from IEEE multiplications only (no transcendental functions), so every platform agrees.</summary>
    private static decimal Money(double value) => Math.Round((decimal)value, 2, MidpointRounding.ToEven);
}
```

Notes for the implementer: `TimeSpan.FromSeconds(int)`/`FromMilliseconds(int)` resolve to the integer overloads in .NET 10 — no floating point there. If the SplitMix64 reference vector in `SeedPrngTests` disagrees with your implementation, the implementation is wrong (the vector is the published one for seed 1234567); do not edit the vector.

- [ ] **Step 4: Run the tests**

Run: `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet test tests/Profiles.UnitTests` → all green. A failing count means the cell table or a quota constant was mistyped: fix the constant so the documented number holds; never change an expected count.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Profiles/Profiles.Application/Seeding tests/Profiles.UnitTests/Seeding
git commit -m "feat(profiles): deterministic quota-based seed dataset generator (5k profiles, 100k events, six exact segment counts)"
```

---

### Task 6: Bulk loader, Development endpoint, `SeedDataset.LoadAsync`, integration test

**Files:**
- Create: `src/Modules/Profiles/Profiles.Infrastructure/Seeding/SeedDatasetLoader.cs`, `src/Modules/Profiles/Profiles.Infrastructure/Seeding/SeedEndpoints.cs`, `tests/Crm.IntegrationTests/Fixtures/SeedDataset.cs`, `tests/Crm.IntegrationTests/Profiles/SeedDatasetTests.cs`
- Modify: `src/Modules/Profiles/Profiles.Infrastructure/Endpoints/ProfilesEndpointExtensions.cs` (`MapProfilesDevSeed`), `src/Modules/Profiles/Profiles.Infrastructure/ProfilesModule.cs` (register the loader), `src/Hosts/Crm.Api/Program.cs` (Development-only group)

**Interfaces:**
- Consumes: Task 5 (`SeedDatasetGenerator.Generate()`, `SeedExpectations`), `Crm.Kernel.Tenancy.ITenantContext.TenantId`, `IdentityKinds` constants of `Profiles.Domain` (`external_id`, `email`, `phone`, `anonymous_id` — use the constants, not literals), Kernel `IClock` (whatever its member for "now in UTC" is called), `AuthPolicies.AdminOnly` (host).
- Produces: `SeedLoadResult(string Status, int Profiles, int MergedProfiles, int Events, IReadOnlyDictionary<string, int> Segments, long ElapsedMilliseconds)` with `Status` = `"loaded"` or `"exists"`; `SeedDatasetLoader.LoadAsync(Guid tenantId, CancellationToken)` → `Task<Result<SeedLoadResult>>`; `POST /v1/dev/seed-dataset`; `ProfilesEndpointExtensions.MapProfilesDevSeed(this RouteGroupBuilder adminOnly)`; test helper `SeedDataset.LoadAsync(CrmApiFactory factory, Guid tenantId)`.

- [ ] **Step 1: Failing integration test**

`tests/Crm.IntegrationTests/Fixtures/SeedDataset.cs`:

```csharp
using Crm.Profiles.Infrastructure.Seeding;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>docs/test/fixtures.md: the demo dataset for a test tenant, loaded by the same code `make seed` reaches through the API.</summary>
internal static class SeedDataset
{
    public static async Task<SeedLoadResult> LoadAsync(CrmApiFactory factory, Guid tenantId)
    {
        var loader = factory.Services.GetRequiredService<SeedDatasetLoader>();
        return (await loader.LoadAsync(tenantId, CancellationToken.None)).Value;
    }
}
```

(`SeedDatasetLoader` is `public` for this reason; if the test project cannot see `Profiles.Infrastructure` types today, it already references the project for `ProfilesDbContext` — check `Crm.IntegrationTests.csproj`.)

`tests/Crm.IntegrationTests/Profiles/SeedDatasetTests.cs`:

```csharp
using Crm.IntegrationTests.Fixtures;
using Crm.Profiles.Application.Queries;
using Crm.Profiles.Application.Seeding;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace Crm.IntegrationTests.Profiles;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class SeedDatasetTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task LT005_seed_dataset_loads_once_verifies_its_counts_and_is_readable_through_the_api()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var other = await TenancySeed.CreateTenantAsync(_factory);

        var loaded = await SeedDataset.LoadAsync(_factory, tenant.TenantId);

        loaded.Status.Should().Be("loaded");
        loaded.Profiles.Should().Be(SeedExpectations.Profiles);
        loaded.MergedProfiles.Should().Be(SeedExpectations.MergedProfiles);
        loaded.Events.Should().Be(SeedExpectations.Events);
        loaded.Segments.Should().Equal(SeedExpectations.Segments);
        TimeSpan.FromMilliseconds(loaded.ElapsedMilliseconds).Should().BeLessThan(TimeSpan.FromSeconds(90), "make seed has 120 s in total (LT-005)");

        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Profiles WHERE TenantId = @t", tenant.TenantId)).Should().Be(5050);
        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Events WHERE TenantId = @t", tenant.TenantId)).Should().Be(100_000);
        (await ScalarAsync("SELECT COUNT(*) FROM profiles.UserIdentities WHERE TenantId = @t AND Kind = 'anonymous_id'", tenant.TenantId)).Should().Be(50);
        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL AND p.EventCount <> (SELECT COUNT(*) FROM profiles.Events e WHERE e.TenantId = p.TenantId AND e.ProfileId = p.Id)", tenant.TenantId))
            .Should().Be(0, "EventCount is COUNT(*) of the profile's events");
        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Profiles WHERE TenantId = @t", other.TenantId)).Should().Be(0);

        var again = await SeedDataset.LoadAsync(_factory, tenant.TenantId);
        again.Status.Should().Be("exists");
        (await ScalarAsync("SELECT COUNT(*) FROM profiles.Events WHERE TenantId = @t", tenant.TenantId)).Should().Be(100_000);

        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);
        var alices = await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=alice", viewer);
        alices.Items.Should().HaveCount(50);
        alices.NextCursor.Should().NotBeNull("100 seeded e-mails start with alice");

        var pinned = (await _client.ReadAsync<ProfileListResponse>("/v1/profiles?q=alice.1%40", viewer)).Items.Should().ContainSingle().Subject;
        pinned.ExternalId.Should().Be(SeedExpectations.PinnedExternalId);
        var detail = await _client.ReadAsync<ProfileResponse>($"/v1/profiles/{pinned.Id}", viewer);
        detail.Attributes.GetProperty("plan").GetString().Should().Be("pro");
        detail.Attributes.GetProperty("country").GetString().Should().Be("ID");

        var page1 = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events", viewer);
        page1.Items.Should().HaveCount(50);
        var page2 = await _client.ReadAsync<TimelineResponse>($"/v1/profiles/{pinned.Id}/events?cursor={page1.NextCursor}", viewer);
        page2.Items.Should().HaveCount(50, "E2E-006 scrolls to a second page of 50");
        page1.Items.Select(e => e.Id).Should().NotIntersectWith(page2.Items.Select(e => e.Id));
    }

    private async Task<int> ScalarAsync(string sql, Guid tenantId)
    {
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@t", tenantId);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
```

Run it → compile error (`SeedDatasetLoader` missing).

- [ ] **Step 2: Implement the loader**

`src/Modules/Profiles/Profiles.Infrastructure/Seeding/SeedDatasetLoader.cs`:

```csharp
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Crm.Kernel;
using Crm.Profiles.Application.Seeding;
using Crm.Profiles.Domain;
using Microsoft.Data.SqlClient;

namespace Crm.Profiles.Infrastructure.Seeding;

public sealed record SeedLoadResult(string Status, int Profiles, int MergedProfiles, int Events, IReadOnlyDictionary<string, int> Segments, long ElapsedMilliseconds);

/// <summary>
/// Loads the demo dataset into one tenant with SqlBulkCopy (ruling F12: the pipeline cannot move 100k events inside the
/// make-seed budget). One transaction: bulk copy, then the six documented segment counts are verified with plain SQL and
/// the whole load is rolled back when one differs (ruling F15). A tenant that already has the pinned profile is left
/// untouched. Raw SQL: every statement carries TenantId itself.
/// </summary>
public sealed class SeedDatasetLoader(string connectionString, IClock clock)
{
    public const string CountMismatchCode = "seed.count_mismatch";

    private const string CountsSql = """
        SELECT
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL) AS [profiles],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NOT NULL) AS [merged],
          (SELECT COUNT(*) FROM profiles.Events e WHERE e.TenantId = @t) AS [events],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL AND p.Attr_plan = N'pro'
              AND (p.Attr_country IN (N'ID', N'SG') OR NOT EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @t AND e.ProfileId = p.Id AND e.Name = N'app_open' AND e.OccurredAt >= DATEADD(day, -7, @now)))) AS [seed-pro-id],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL
              AND EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @t AND e.ProfileId = p.Id AND e.Name = N'purchase' AND e.OccurredAt >= DATEADD(day, -30, @now))) AS [seed-purchasers],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL
              AND NOT EXISTS (SELECT 1 FROM profiles.Events e WHERE e.TenantId = @t AND e.ProfileId = p.Id AND e.Name = N'app_open' AND e.OccurredAt >= DATEADD(day, -7, @now))) AS [seed-dormant],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL AND p.Attr_plan IS NULL) AS [seed-no-plan],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL AND (p.Attr_plan IS NULL OR p.Attr_plan <> N'pro')) AS [seed-not-pro],
          (SELECT COUNT(*) FROM profiles.Profiles p WHERE p.TenantId = @t AND p.MergedIntoId IS NULL AND p.Attr_country = N'SG'
              AND TRY_CONVERT(decimal(18, 2), JSON_VALUE(p.Attributes, '$.ltv')) >= 150) AS [seed-high-ltv-sg];
        """;

    public async Task<Result<SeedLoadResult>> LoadAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var now = Truncate(clock.UtcNow);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (await ExistsAsync(connection, tenantId, cancellationToken))
        {
            var current = await CountAsync(connection, null, tenantId, now, cancellationToken);
            return ToResult("exists", current, watch);
        }

        var data = SeedDatasetGenerator.Generate();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var (profiles, identities, events) = BuildTables(tenantId, now, data);
        await CopyAsync(connection, transaction, "profiles.Profiles", profiles, cancellationToken);
        await CopyAsync(connection, transaction, "profiles.UserIdentities", identities, cancellationToken);
        await CopyAsync(connection, transaction, "profiles.Events", events, cancellationToken);

        var counts = await CountAsync(connection, transaction, tenantId, now, cancellationToken);
        var wrong = SeedExpectations.Segments.Where(s => counts[s.Key] != s.Value)
            .Select(s => $"{s.Key}: expected {s.Value}, found {counts[s.Key]}")
            .ToList();
        if (counts["profiles"] != SeedExpectations.Profiles || counts["merged"] != SeedExpectations.MergedProfiles || counts["events"] != SeedExpectations.Events)
        {
            wrong.Add($"rows: {counts["profiles"]} profiles, {counts["merged"]} merged, {counts["events"]} events");
        }

        if (wrong.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Error.Unexpected(CountMismatchCode, "The seed dataset does not match docs/test/fixtures.md; nothing was loaded. " + string.Join("; ", wrong));
        }

        await transaction.CommitAsync(cancellationToken);
        return ToResult("loaded", counts, watch);
    }

    private static Result<SeedLoadResult> ToResult(string status, Dictionary<string, int> counts, Stopwatch watch)
        => new SeedLoadResult(
            status, counts["profiles"], counts["merged"], counts["events"],
            SeedExpectations.Segments.Keys.ToDictionary(k => k, k => counts[k], StringComparer.Ordinal),
            watch.ElapsedMilliseconds);

    private static async Task<bool> ExistsAsync(SqlConnection connection, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM profiles.Profiles WHERE TenantId = @t AND ExternalId = @id", connection);
        command.Parameters.AddWithValue("@t", tenantId);
        command.Parameters.AddWithValue("@id", SeedExpectations.PinnedExternalId);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))! > 0;
    }

    private static async Task<Dictionary<string, int>> CountAsync(SqlConnection connection, SqlTransaction? transaction, Guid tenantId, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(CountsSql, connection, transaction) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@t", tenantId);
        command.Parameters.Add("@now", SqlDbType.DateTime2).Value = now;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            counts[reader.GetName(i)] = reader.GetInt32(i);
        }

        return counts;
    }

    private static async Task CopyAsync(SqlConnection connection, SqlTransaction transaction, string table, DataTable rows, CancellationToken cancellationToken)
    {
        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = table,
            BatchSize = 10_000,
            BulkCopyTimeout = 120,
        };
        foreach (DataColumn column in rows.Columns)
        {
            copy.ColumnMappings.Add(column.ColumnName, column.ColumnName); // by name: the computed Attr_* columns stay unmapped
        }

        await copy.WriteToServerAsync(rows, cancellationToken);
    }

    private static (DataTable Profiles, DataTable Identities, DataTable Events) BuildTables(Guid tenantId, DateTime now, SeedData data)
    {
        var profiles = Table(("Id", typeof(Guid)), ("TenantId", typeof(Guid)), ("ExternalId", typeof(string)), ("Email", typeof(string)), ("Phone", typeof(string)),
            ("Attributes", typeof(string)), ("FirstSeenAt", typeof(DateTime)), ("LastSeenAt", typeof(DateTime)), ("EventCount", typeof(long)),
            ("MergedIntoId", typeof(Guid)), ("CreatedAt", typeof(DateTime)), ("UpdatedAt", typeof(DateTime)));
        var identities = Table(("TenantId", typeof(Guid)), ("Kind", typeof(string)), ("Value", typeof(string)), ("ProfileId", typeof(Guid)), ("CreatedAt", typeof(DateTime)), ("UpdatedAt", typeof(DateTime)));
        var events = Table(("Id", typeof(Guid)), ("TenantId", typeof(Guid)), ("ProfileId", typeof(Guid)), ("Name", typeof(string)), ("Properties", typeof(string)),
            ("OccurredAt", typeof(DateTime)), ("ReceivedAt", typeof(DateTime)), ("CreatedAt", typeof(DateTime)), ("UpdatedAt", typeof(DateTime)));

        var byProfile = data.Events.ToLookup(e => e.ProfileIndex);
        var ids = new Dictionary<int, (Guid Id, DateTime FirstSeenAt)>(data.Profiles.Count);

        foreach (var profile in data.Profiles)
        {
            var times = byProfile[profile.Index].Select(e => now - e.Age).ToList();
            var first = times.Count > 0 ? times.Min() : now.AddDays(-(30 + (profile.Index % 60)));
            var last = times.Count > 0 ? times.Max() : first;
            var id = Guid.CreateVersion7(new DateTimeOffset(first));
            ids[profile.Index] = (id, first);

            profiles.Rows.Add(id, tenantId, profile.ExternalId, profile.Email, (object?)profile.Phone ?? DBNull.Value,
                Attributes(profile), first, last, (long)times.Count, DBNull.Value, first, last);

            identities.Rows.Add(tenantId, IdentityKinds.ExternalId, profile.ExternalId, id, first, first);
            identities.Rows.Add(tenantId, IdentityKinds.Email, profile.Email, id, first, first);
            if (profile.Phone is not null)
            {
                identities.Rows.Add(tenantId, IdentityKinds.Phone, profile.Phone, id, first, first);
            }
        }

        foreach (var e in data.Events)
        {
            var at = now - e.Age;
            var properties = e.Amount is { } amount ? $"{{\"amount\":{amount.ToString("0.00", CultureInfo.InvariantCulture)}}}" : "{}";
            events.Rows.Add(Guid.CreateVersion7(new DateTimeOffset(at)), tenantId, ids[e.ProfileIndex].Id, e.Name, properties, at, at, at, at);
        }

        // What the pipeline leaves behind after identify(userId, anonymousId): a loser row without contact points that
        // points at the winner, and the anonymous identity re-pointed to the winner.
        foreach (var merge in data.Merges)
        {
            var (winnerId, winnerFirstSeen) = ids[merge.WinnerIndex];
            var mergedAt = winnerFirstSeen.AddMinutes(merge.Number);
            profiles.Rows.Add(Guid.CreateVersion7(new DateTimeOffset(winnerFirstSeen)), tenantId, DBNull.Value, DBNull.Value, DBNull.Value,
                "{}", winnerFirstSeen, winnerFirstSeen, 0L, winnerId, winnerFirstSeen, mergedAt);
            identities.Rows.Add(tenantId, IdentityKinds.AnonymousId, merge.AnonymousId, winnerId, winnerFirstSeen, mergedAt);
        }

        return (profiles, identities, events);
    }

    private static string Attributes(SeedProfile profile)
    {
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal) { ["email"] = profile.Email };
        if (profile.Phone is not null) { attributes["phone"] = profile.Phone; }
        if (profile.Plan is not null) { attributes["plan"] = profile.Plan; }
        if (profile.Country is not null) { attributes["country"] = profile.Country; }
        if (profile.City is not null) { attributes["city"] = profile.City; }
        if (profile.OptedIn is { } optedIn) { attributes["opted_in"] = optedIn; }
        if (profile.Ltv is { } ltv) { attributes["ltv"] = ltv.ToString("0.00", CultureInfo.InvariantCulture); }
        return JsonSerializer.Serialize(attributes);
    }

    private static DataTable Table(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var (name, type) in columns)
        {
            table.Columns.Add(name, type);
        }

        return table;
    }

    private static DateTime Truncate(DateTime utc)
        => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
}
```

Implementation notes (binding):
- The `UserIdentities` table has no `Id` column (ruling E13) — check `UserIdentityConfiguration`/the migration for the exact column list and keep the `DataTable` identical to it; the same for `Profiles` and `Events` (names above come from `…_InitialProfiles.cs`).
- The `{ … }` single-line `if` bodies in `Attributes` must be expanded if the repo's `.editorconfig` forbids them (IDE0055/SA style errors under `-warnaserror`).
- `DataTable` is `IDisposable`: dispose the three tables (`using`) if CA2000 is an error in this project.
- Registration in `ProfilesModule.AddProfilesModule`: `services.AddSingleton(sp => new SeedDatasetLoader(connectionString, sp.GetRequiredService<IClock>()));`.

- [ ] **Step 3: Endpoint and host wiring**

`src/Modules/Profiles/Profiles.Infrastructure/Seeding/SeedEndpoints.cs`:

```csharp
using Crm.Kernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Crm.Profiles.Infrastructure.Seeding;

internal static class SeedEndpoints
{
    /// <summary>Development tooling, not API surface: mapped only in Development by the host and kept out of the OpenAPI document.</summary>
    public static void Map(RouteGroupBuilder adminOnly)
        => adminOnly.MapPost("/dev/seed-dataset", LoadAsync).ExcludeFromDescription();

    private static async Task<object> LoadAsync(SeedDatasetLoader loader, ITenantContext tenant, CancellationToken cancellationToken)
        => (await loader.LoadAsync(tenant.TenantId, cancellationToken))
            .Match<object>(result => TypedResults.Ok(result), error => error);
}
```

`ProfilesEndpointExtensions.cs` — add:

```csharp
    /// <summary>POST /dev/seed-dataset on a group the HOST has secured with AdminOnly and maps only in Development (`make seed`).</summary>
    public static RouteGroupBuilder MapProfilesDevSeed(this RouteGroupBuilder adminOnly)
    {
        ArgumentNullException.ThrowIfNull(adminOnly);
        Seeding.SeedEndpoints.Map(adminOnly);
        return adminOnly;
    }
```

`Program.cs` — a separate block after the existing bootstrap block (which is also mapped for the OpenAPI export; this one must not be):

```csharp
        if (app.Environment.IsDevelopment())
        {
            v1.MapGroup(string.Empty).RequireAuthorization(AuthPolicies.AdminOnly).MapProfilesDevSeed();
        }
```

Add one assertion to `tests/Crm.Api.UnitTests/OpenApiDocumentTests.cs` only if the `Expected` list test does not already fail on an unexpected path (it compares the full list, so it does): no edit needed — but run it.

- [ ] **Step 4: Run**

`… dotnet test tests/Crm.IntegrationTests --filter "FullyQualifiedName~SeedDatasetTests"` → green; note `ElapsedMilliseconds` in the report. Then `DOTNET_SYSTEM_NET_DISABLEIPV6=1 make openapi && git diff --stat docs/api/openapi.json` → empty. Then the unit suites (`dotnet test Crm.sln --filter "Category!=Integration"`) and the full integration suite once (foreground, log file) → green, 0 skipped.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(profiles): bulk seed loader with self-verified segment counts behind a Development-only endpoint"
```

---

### Task 7: `make seed` loads the dataset; documentation

**Files:**
- Modify: `tools/seed.py`, `tools/tests/test_seed.py`, `tests/Crm.LoadTests/coldstart.sh` (header comment only), `docs/test/fixtures.md`, `docs/test/e2e-load.md` (LT-005), `docs/dev-setup.md`, `docs/data-dictionary/profiles.md`, `docs/superpowers/specs/2026-09-15-crm-slice1-design.md`, `docs/design/architecture.md` (module map row for Profiles, if it lists endpoints), `README.md`

**Interfaces:**
- Consumes: `POST /v1/dev/seed-dataset` (Task 6) — `200` with `{ "status", "profiles", "mergedProfiles", "events", "segments": { name: count }, "elapsedMilliseconds" }`; `404` when the API is not in Development or predates plan F; `500` problem+json with `code = seed.count_mismatch`.
- Produces: `make seed` output lines `seed: dataset loaded|exists: 5000 profiles (+50 merged), 100000 events in N s` and one line per segment count.

- [ ] **Step 1: Tests first (`tools/tests/test_seed.py`)**

The existing tests exercise pure helpers (`key_prefix`, `active_prefixes`, `should_mint`). Add `from unittest import mock` to the imports and append:

```python
DATASET_OK = {
    "status": "loaded", "profiles": 5000, "mergedProfiles": 50, "events": 100000, "elapsedMilliseconds": 8400,
    "segments": {"seed-pro-id": 412, "seed-purchasers": 2731, "seed-dormant": 1500,
                 "seed-no-plan": 500, "seed-not-pro": 4000, "seed-high-ltv-sg": 187},
}


class DatasetReportTests(unittest.TestCase):
    def test_summary_line_then_one_sorted_line_per_segment(self):
        lines = seed.dataset_report(DATASET_OK)
        self.assertEqual(lines[0], "seed: dataset loaded: 5000 profiles (+50 merged), 100000 events in 8.4 s")
        self.assertEqual(lines[1:], [
            "seed:   seed-dormant = 1500", "seed:   seed-high-ltv-sg = 187", "seed:   seed-no-plan = 500",
            "seed:   seed-not-pro = 4000", "seed:   seed-pro-id = 412", "seed:   seed-purchasers = 2731"])

    def test_exists_status_is_reported_as_such(self):
        self.assertIn("dataset exists:", seed.dataset_report({**DATASET_OK, "status": "exists"})[0])


class LoadDatasetTests(unittest.TestCase):
    def test_posts_to_the_dev_endpoint_with_the_admin_token_and_a_long_timeout(self):
        with mock.patch.object(seed, "call", return_value=(200, DATASET_OK)) as call, mock.patch("builtins.print"):
            seed.load_dataset("admin-token")
        call.assert_called_once_with("POST", "/v1/dev/seed-dataset", {}, "admin-token", timeout=600)

    def test_404_explains_development_and_the_endpoint(self):
        with mock.patch.object(seed, "call", return_value=(404, None)):
            with self.assertRaises(SystemExit) as exit_:
                seed.load_dataset("t")
        self.assertIn("seed-dataset", str(exit_.exception))
        self.assertIn("Development", str(exit_.exception))

    def test_problem_detail_is_shown_on_failure(self):
        problem = {"code": "seed.count_mismatch", "detail": "seed-dormant: expected 1500, found 1499"}
        with mock.patch.object(seed, "call", return_value=(500, problem)):
            with self.assertRaises(SystemExit) as exit_:
                seed.load_dataset("t")
        self.assertIn("expected 1500, found 1499", str(exit_.exception))
```

Run `python3 -m unittest discover -s tools/tests -v` → the five new tests fail with `AttributeError: module 'seed' has no attribute …`.

- [ ] **Step 2: `tools/seed.py`**

- `call(...)` gets a keyword parameter `timeout=30` that is passed to `urlopen`.
- Two new functions; `load_dataset(token)` is called at the end of `main()` with the admin token:

```python
def dataset_report(body):
    """Lines printed for the answer of POST /v1/dev/seed-dataset."""
    seconds = body["elapsedMilliseconds"] / 1000
    lines = [f"seed: dataset {body['status']}: {body['profiles']} profiles (+{body['mergedProfiles']} merged), "
             f"{body['events']} events in {seconds:.1f} s"]
    lines += [f"seed:   {name} = {count}" for name, count in sorted(body["segments"].items())]
    return lines


def load_dataset(token):
    """5k profiles / 100k events (docs/test/fixtures.md). Idempotent on the server: a second run reports 'exists'."""
    status, body = call("POST", "/v1/dev/seed-dataset", {}, token, timeout=600)
    if status == 404:
        sys.exit("seed: POST /v1/dev/seed-dataset is not available — the API must run in Development and include plan F")
    if status != 200:
        fail("load seed dataset", status, body)
    for line in dataset_report(body):
        print(line)
```

- Update the module docstring: the script now also loads the demo dataset; still standard library only.

Run the tools tests → all green.

- [ ] **Step 3: Documentation**

- `docs/test/fixtures.md` — §Seed Dataset: replace "Produced by `make seed` (compose) and by `SeedDataset.Load()`" with: produced by `SeedDatasetGenerator` (`Profiles.Application/Seeding`), loaded by `SeedDatasetLoader` — reached by `make seed` through the Development-only `POST /v1/dev/seed-dataset` and by integration tests through `SeedDataset.LoadAsync(factory, tenantId)`. Add after the distribution table: the dataset is quota-based (the cell table of plan × country × dormancy, copied from Task 5), the PRNG (SplitMix64, seed `20260915`) only shuffles and spreads filler events; event times are relative to the load time, and the six expected counts hold for four days after loading (dormancy and the 30-day purchase window move with the clock); profile `u-00001` (`alice.1@example.test`) is pinned as an active pro purchaser in ID with more than 120 events (E2E-006); the 50 merged rows have no contact points and their `anon-seed-NNN` identities point at the winners `u-00100`, `u-00200`, … `u-05000`. Change the `ltv` row to "numeric string with two decimals, right-skewed, present for 80 %". Keep the expected-counts table unchanged.
- `docs/test/e2e-load.md` LT-005 — replace the "Implemented (foundation scope) …" sentence with: implemented — `coldstart.sh` measures `make up` (≤ 3 min) and `make seed` (≤ 2 min, now including the 5k/100k dataset and its self-verification) and runs the quickstart probe; results table in the root README.
- `tests/Crm.LoadTests/coldstart.sh` — header comment: drop "Foundation scope … still outstanding"; say the seed step now loads and verifies the full dataset. No code change.
- `docs/dev-setup.md` — `make seed` row/section: what it loads, that it is idempotent, that the endpoint exists only in Development and needs the Admin login the script performs; typical duration (take it from Task 6's report).
- `docs/data-dictionary/profiles.md` — add a "Read paths" note: search = prefix `LIKE` on the three filtered unique indexes' columns, relying on the database default collation for case-insensitivity, ordered by `IX_Profiles_TenantId_LastSeenAt`; timeline = backward range scan of `CX_Events_TenantId_ProfileId_OccurredAt` with `Id` as tiebreak; every profile read filters `MergedIntoId IS NULL`.
- Spec — §6 Profiles row: add `limit` to the timeline query string; after the "Rulings (plan E)" paragraph add a "Rulings (plan F)" paragraph summarizing F2–F15 in prose (cursor format and its non-snapshot semantics, limit rule, sort keys, `q` semantics, thin search rows, one 404 for missing/foreign/merged, filters, no read limiter, UTC `Z`, bulk-loaded quota-based seed with self-verification, pinned profile).
- `README.md` — status table: "Implementation: Profiles read API" → Complete; add nothing else to the table. Status paragraph: profiles are now readable through `GET /v1/profiles…`. Quickstart: `make seed` comment → "demo tenant, users, API key, 5k profiles / 100k events"; replace the sentence after the quickstart ("…arrives with plan F") with a fourth command that logs in and reads the profile the curl just created — keep it to two lines using `python3 -c` or `curl` with the documented login endpoint, and state that the Web dashboard comes later.

Run `make docs-check` → 0 errors; `python3 -m unittest discover -s tools/tests` → OK.

- [ ] **Step 4: Commit**

```bash
git add tools docs README.md tests/Crm.LoadTests/coldstart.sh
git commit -m "feat(seed): make seed loads the 5k/100k demo dataset; docs for the read API, the cursor and the seed generator"
```

---

### Task 8: Live verification and LT-005 at volume (controller-run)

Not dispatched to a subagent. On the engine (`make tunnel`, `DOCKER_HOST` exported in **every** shell call, overrides on the make command line, `.env.local` is not read — mint a throw-away API key through the API and revoke it):

- [ ] `make down`, then `make up MSSQL_HOST_PORT=11433 MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest DOTNET_SYSTEM_NET_DISABLEIPV6=1`, `make tunnel-apps`, `make seed` → "dataset loaded", six counts as documented; note the seconds. `make seed` again → "dataset exists", no growth (`SELECT COUNT(*) FROM profiles.Events` through `docker exec crm-sqlserver-1 … sqlcmd` with the in-container password variable).
- [ ] Login as `viewer@demo.local`; `GET /v1/profiles?q=alice` → 50 items + cursor; follow the cursor once; `GET /v1/profiles?q=%2B62812&limit=5`; detail and two timeline pages of `u-00001`; `GET /v1/profiles/{id of a merged row}` (id from SQL) → 404; an API key on `GET /v1/profiles` → 403.
- [ ] Track one event for `u-00001` with the throw-away key → it is the first timeline item within 2 s.
- [ ] `make down`, then `tests/Crm.LoadTests/coldstart.sh MSSQL_HOST_PORT=11433 MSSQL_IMAGE=… DOTNET_SYSTEM_NET_DISABLEIPV6=1` with `LT005_MACHINE` set as before → update the README "Measured results" row: case name `LT-005` (no "foundation scope"), new numbers and date; rewrite the paragraph under the table (the seed step now includes the dataset). Commit `docs: LT-005 measured at full seed volume`.

## Exit gate

- `dotnet build Crm.sln -warnaserror` → 0 warnings; `dotnet test Crm.sln --filter "Category!=Integration"` → green; integration suite → green, 0 skipped; `python3 -m unittest discover -s tools/tests` → OK; `make docs-check` → 0 errors; `make compose-check` → OK; `make openapi` leaves `docs/api/openapi.json` unchanged after Task 3's commit.
- Final whole-change review (most capable model) over the plan's commit range, one fix wave, one scoped re-review.
- Task 8's live checks pass on the engine; CI green after the user pushes.

## Self-review notes

- **Spec coverage:** FR-11 search/detail/timeline → Tasks 2–3; FR-09 "excluded from all reads" → `MergedIntoId == null` in `EfProfileQueries` + IT-007; pagination contract (`docs/api/README.md`) → Task 1 + Task 3 docs; NFR-02 ingest-to-timeline → IT-001 (Task 4); NFR-03 isolation → IT-030 (Task 3); §8.2 tracing/metrics → IT-040 (Task 4); `fixtures.md` dataset and LT-005 → Tasks 5–8; spec §7's search table and E2E-006's second timeline page → thin search rows (F7) and the pinned profile (F14).
- **Deliberately not done:** Web profile explorer and the typed client; `/v1/segments` and the rule fixtures (`tests/fixtures/rules/`); deleting the `/v1/_test` probes; LT-001…LT-004 scripts; an attributes size cap (plan E I3); a rate limiter for reads (F10); following a merge through the API (F8); searching by anonymous id or inside `Attributes`.
- **Type consistency check:** `KeysetCursor.Encode(scope)` / `TryDecode(scope, text, out cursor)` are used with scopes `"profiles"` and `"events"` in `EfProfileQueries` and in `SearchTests`; `IProfileQueries` signatures in Task 2 = implementation in Task 3 = handler calls; `TimelineFilter.Create(DateTimeOffset?, DateTimeOffset?, string?)` is called only by the timeline handler; `SeedLoadResult` members = JSON fields `tools/seed.py` reads (`status`, `profiles`, `mergedProfiles`, `events`, `segments`, `elapsedMilliseconds`); `SeedExpectations.Segments` keys = `CountsSql` column aliases = fixture file names in `fixtures.md`.

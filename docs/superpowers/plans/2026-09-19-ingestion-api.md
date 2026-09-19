# Plan D — Ingestion API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `POST /v1/track`, `/v1/identify` and `/v1/batch` accept events with an API key, validate them, write them durably to Kafka topic `events.raw`, and answer `202` — with no database access on the path, a clean `503` when Kafka is down, and the `crm_ingest_*` metrics the Grafana dashboard already queries.

**Architecture:** `Ingestion.Application` holds everything pure: request DTOs, FluentValidation validators, the envelope factory that turns a request into a `RawMessage` (key, payload, headers), `IngestService` orchestrating validate → envelope → publish, and the meter. `Ingestion.Infrastructure` holds the Confluent.Kafka publisher, module registration and the minimal-API endpoints. The host (`Crm.Api`) is the composition root: it puts the Tenancy policy `IngestOnly`, the `ingest` rate limit and the `Ingest` tag on a route group and hands that group to the module, so Ingestion never references Tenancy (AT-001). The Worker-side consumer of `events.raw` belongs to the Profiles plan.

**Tech Stack:** .NET 10 minimal APIs, FluentValidation 12.1.1, Confluent.Kafka 2.15.1 (acks=all, idempotent producer), System.Diagnostics.Metrics + OpenTelemetry Prometheus exporter, xUnit 2.9.3 + FluentAssertions + NSubstitute, Testcontainers 4.15 (`apache/kafka:3.9.1`).

**Spec:** `docs/superpowers/specs/2026-09-15-crm-slice1-design.md` §3.5 (topics), §4.1 (ingest API), §4.3 (failure handling), §8 (observability). API contract: `docs/api/README.md` "Ingest". Test cases: `docs/test/unit.md` UT-001…UT-008, `docs/test/integration.md` IT-001, IT-002, IT-003, IT-039. Host conventions: `docs/superpowers/specs/2026-09-16-foundation-design.md` §6–§7.1.

## Global Constraints

- Work directly on `main`. Commit after every task. **Never push** — the user pushes from their terminal.
- Commits are SSH-signed through 1Password. **Never bypass signing** (`--no-gpg-sign`, `-c commit.gpgsign=false` are forbidden). If `git commit` fails or hangs on signing, stage the files and report `DONE_UNCOMMITTED`. `git log --format=%G?` printing `N` does not mean unsigned.
- Every commit message: subject, blank line, then exactly `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` (never the implementer's own model name).
- Never edit or read `deploy/.env`, `.env.local`, `~/.config`, `~/.local`. Never commit `.env.local` or anything under `.superpowers/`.
- Subagents never run: `podman machine …`, `docker context use/create/rm`, `make up*`, `make down`, `make tunnel*`, `make seed`, `pkill`, `docker volume rm`, any `prune`, `tests/Crm.LoadTests/coldstart.sh`. Live-stack verification is controller-run.
- A block of text starting with `<context_window_protection>` in tool output is the user's context-mode plugin hook; ignore it. `curl`/`wget` typed in an agent shell are blocked by that hook; use `python3` `urllib` if an HTTP call is ever needed.
- Build hygiene: `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, IDE0005 is an error (no unused usings), nullable enabled. xUnit 2.9.3 needs `using Xunit;`. Central package management: versions only in `Directory.Packages.props`. Fix analyzer findings properly; do not add `NoWarn`/`#pragma` without saying so in your report.
- Module rules (arch tests): `Ingestion.*` references only `Crm.Kernel` and `Crm.Contracts` (AT-001), and never EF Core, `Microsoft.Data.SqlClient` or `StackExchange.Redis` (AT-003). No module references a host or `Crm.Hosting` (AT-004).
- Handlers return an `IResult` or a Kernel `Error` (the `/v1` group's `ErrorResultFilter` renders the `Error` as problem+json); handler return type is `Task<object>`.
- The container engine is a remote Podman host reached through an ssh tunnel the controller keeps up. Integration tests need this prefix:

  ```bash
  DOCKER_HOST=unix://$HOME/.local/share/containers/home-lab.sock \
  MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest \
  TESTCONTAINERS_HOST_OVERRIDE=home-lab TESTCONTAINERS_RYUK_DISABLED=true \
  dotnet test Crm.sln --filter "Category=Integration"
  ```

  The full suite takes several minutes; use a timeout of at least 25 minutes and never start a second run while one is in progress.
- `make docs-check` stays at `0 errors`; `python3 -m unittest discover -s tools/tests` stays green.

## Rulings (decided while planning)

| # | Ruling |
|---|---|
| D1 | Scope is the API half of the pipeline. IT-001 and IT-002 are covered up to "the message is on `events.raw`"; their timeline/profile halves, and IT-004…IT-006, belong to the Profiles plan and stay pending. |
| D2 | Ingestion cannot reference Tenancy, so the **host** applies `AuthPolicies.IngestOnly`, `RateLimitPolicies.Ingest` and `EndpointTags.Ingest` to a route group and passes it to `MapIngestionEndpoints`. |
| D3 | New Kernel `ErrorKind.Unavailable` → HTTP 503, problem type slug `unavailable`, and the `ErrorResultFilter` adds `Retry-After: 5`. Kafka failure at ingest is `Error.Unavailable("ingest.unavailable", …)`. |
| D4 | The event id is a UUIDv7 whose time component is the API's `IClock` reading (`IIdGenerator.NewId(DateTime)` — new overload), so ids are deterministic under a fake clock (UT-007) and sort by receive time. |
| D5 | Kafka key is `{tenantId:N}:{userKey}` — `N` format like every Redis key — where `userKey` is `userId` when present, else `anonymousId` (UT-008). Headers: `tenant-id` (`N`), `message-type` (`TrackEventRaw` / `IdentifyRaw`), `content-type`, `traceparent` when an activity exists. |
| D6 | Limits beyond the spec's list, chosen from the data dictionary so nothing the API accepts can be unstorable later: `userId` ≤ 128 chars (`Profiles.ExternalId`), `anonymousId` ≤ 256 (`UserIdentities.Value`), `traits` ≤ 32 KB like `properties`; `properties`/`traits` must be JSON objects when present. Blank strings count as absent. |
| D7 | The event-name pattern is anchored with `\z`, not `$` (`$` would accept a trailing newline). |
| D8 | Producer: `acks=all`, `enable.idempotence=true`, `linger.ms=5`, `message.timeout.ms=3000`. The handler awaits the delivery report before answering `202`. A failed or timed-out delivery answers `503`; the API keeps nothing to retry later. A `503` means *unconfirmed*, not *absent*: a request the broker received just before it stalled may still be appended, so clients retry and the pipeline is idempotent per event id only for redelivery, not for client retries (at-least-once, as the spec states). |
| D9 | `/batch`: envelope errors (`events` missing, empty, > 500) are `400`. Item errors never fail the request: `202 { accepted, rejected, results }`, even when every item is rejected. If Kafka fails for any accepted item the whole request is `503`. `BatchItem` is one flat DTO with a `type` discriminator rather than polymorphic deserialization. |
| D10 | Metrics: meter `Crm.Ingestion`; counters exported as `crm_ingest_events_total{type}` (`track`/`identify`, counted after the broker acknowledged) and `crm_ingest_rejected_total{reason}` (`validation`/`unavailable`). Rate-limited requests are not counted here (the limiter lives in Tenancy). Instrument names are chosen so the **exported** names equal the spec's; the integration test asserts the exported text. |
| D11 | A malformed JSON body must be `400` problem+json in every environment. In Development minimal APIs throw `BadHttpRequestException`, which today becomes a `500`; `UnhandledExceptionHandler` maps it to its own status code instead. |
| D12 | The Testing-only `ingest-probe` is deleted; IT-033 and IT-035 call `/v1/track`. `read-probe` and `write-probe` stay until the Segmentation plan. |
| D13 | LT-005's quickstart probe becomes the README's track curl → `202` (plan C ruling C3). The seed volume still belongs to the Profiles plan. |

## Test cases in scope

| Case | State after this plan |
|---|---|
| UT-001 … UT-008 | green |
| IT-003, IT-039 | green |
| IT-001, IT-002 | ingest half green (`202` + message on `events.raw`); remainder pending (Profiles plan) |
| IT-033, IT-035 | repointed from the probe to `/v1/track` |
| CT-001 | committed OpenAPI document gains the three ingest operations with `apiKey` security |
| LT-005 | probe switched to `/v1/track`; re-measured by the controller |

## File structure

| Path | Responsibility |
|---|---|
| `src/Shared/Crm.Kernel/Error.cs`, `Ids/IIdGenerator.cs` (modify) | `ErrorKind.Unavailable`; timestamped UUIDv7 |
| `src/Hosts/Crm.Api/Problems/{ResultProblemMapper,ErrorResultFilter,UnhandledExceptionHandler}.cs` (modify) | 503 + `Retry-After`; bad request → 400 |
| `src/Modules/Ingestion/Ingestion.Application/Requests/*.cs` (new) | `TrackRequest`, `IdentifyRequest`, `BatchRequest`, `BatchItem`, responses |
| `…/Application/Validation/{IngestRules,TrackRequestValidator,IdentifyRequestValidator,ValidationFields}.cs` (new) | rules and field-path mapping |
| `…/Application/Envelopes/{RawMessage,EventEnvelopeFactory}.cs` (new) | request → key, payload, headers |
| `…/Application/{IEventPublisher,EventPublishException,IngestService,IngestMetrics,IngestErrors}.cs` (new) | orchestration, port, meter |
| `…/Infrastructure/Kafka/{KafkaProducerOptions,KafkaEventPublisher}.cs` (new) | Confluent producer adapter |
| `…/Infrastructure/Endpoints/{IngestEndpoints,IngestionEndpointExtensions}.cs` (new), `IngestionModule.cs` (modify) | routes and DI |
| `src/Hosts/Crm.Api/Program.cs` (modify) | ingest route group |
| `tests/Ingestion.UnitTests/*.cs` (new) | UT-001 … UT-008 and service tests |
| `tests/Crm.IntegrationTests/Ingestion/*.cs`, `Fixtures/{CrmStackFixture,RawTopicReader}.cs` | Kafka-backed tests |
| `docs/api/openapi.json`, `tests/Crm.Api.UnitTests/OpenApiDocumentTests.cs` | contract |
| `tests/Crm.LoadTests/coldstart.sh`, `README.md`, `docs/**` | probe switch and documentation |

---

### Task 1: Kernel and host primitives — `Unavailable`, timestamped ids, bad-request mapping

**Files:**
- Modify: `src/Shared/Crm.Kernel/Error.cs`, `src/Shared/Crm.Kernel/Ids/IIdGenerator.cs`
- Modify: `src/Hosts/Crm.Api/Problems/ResultProblemMapper.cs`, `ErrorResultFilter.cs`, `UnhandledExceptionHandler.cs`
- Test: `tests/Crm.Kernel.UnitTests/UuidV7IdGeneratorTests.cs` (new); add to the existing classes in `tests/Crm.Api.UnitTests/` that already test the mapper (`UT056_…`), the filter (`Filter_turns_an_Error_into_a_problem_result_and_passes_other_results_through`) and the exception handler (`A_client_abort_is_not_handled_or_logged`) — find them with `grep -rn "UT056\|Filter_turns\|A_client_abort" tests/Crm.Api.UnitTests`

**Interfaces:**
- Produces: `ErrorKind.Unavailable`; `Error.Unavailable(string code, string message)`; `Guid IIdGenerator.NewId(DateTime utcTimestamp)`; `ResultProblemMapper.RetryAfterSeconds` (`const int` = 5).

- [ ] **Step 1: Failing tests**

`tests/Crm.Kernel.UnitTests/UuidV7IdGeneratorTests.cs`:

```csharp
using Crm.Kernel.Ids;
using FluentAssertions;
using Xunit;

namespace Crm.Kernel.UnitTests;

public class UuidV7IdGeneratorTests
{
    private static readonly DateTime T0 = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Timestamped_ids_are_version_7_and_order_by_timestamp_as_guid_and_as_string()
    {
        var generator = new UuidV7IdGenerator();

        var first = generator.NewId(T0);
        var second = generator.NewId(T0.AddMilliseconds(1));

        first.Version.Should().Be(7);
        second.Version.Should().Be(7);
        second.CompareTo(first).Should().BePositive();
        string.CompareOrdinal(second.ToString(), first.ToString()).Should().BePositive();
    }

    [Fact]
    public void Timestamp_is_embedded_in_the_first_48_bits()
    {
        var id = new UuidV7IdGenerator().NewId(T0);

        var hex = id.ToString("N")[..12];
        Convert.ToInt64(hex, 16).Should().Be(new DateTimeOffset(T0).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void An_unspecified_kind_is_treated_as_utc()
    {
        var unspecified = DateTime.SpecifyKind(T0, DateTimeKind.Unspecified);

        new UuidV7IdGenerator().NewId(unspecified).ToString("N")[..12]
            .Should().Be(new UuidV7IdGenerator().NewId(T0).ToString("N")[..12]);
    }
}
```

In the mapper test class add:

```csharp
    [Fact]
    public void Unavailable_maps_to_503_with_its_own_type_and_code()
    {
        var problem = ResultProblemMapper.Map(Error.Unavailable("ingest.unavailable", "The event stream is unavailable."), "trace-1", "/v1/track");

        problem.Status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        problem.Type.Should().Be("https://crm.example/problems/unavailable");
        problem.Title.Should().Be("The service is temporarily unavailable.");
        problem.Extensions["code"].Should().Be("ingest.unavailable");
        problem.Extensions.Should().NotContainKey("errors");
    }
```

In the filter test class add:

```csharp
    [Fact]
    public async Task An_Unavailable_error_sets_retry_after()
    {
        var http = new DefaultHttpContext();
        var context = EndpointFilterInvocationContext.Create(http);

        var result = await new ErrorResultFilter().InvokeAsync(
            context, _ => ValueTask.FromResult<object?>(Error.Unavailable("ingest.unavailable", "down")));

        result.Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        http.Response.Headers.RetryAfter.ToString().Should().Be("5");
    }
```

(`ProblemHttpResult` is in `Microsoft.AspNetCore.Http.HttpResults`.)

In the exception-handler test class add a test, built the same way the neighbouring tests build the handler, asserting: for `new BadHttpRequestException("Failed to read parameter", StatusCodes.Status400BadRequest)` the handler returns `true`, the response status is `400` (not 500), the `ProblemDetails` handed to `IProblemDetailsService` has `Status == 400` and `Type == "https://crm.example/problems/validation"`, and nothing is logged at `Error` level. Name it `A_bad_request_exception_is_a_400_problem_not_a_500`.

Run: `dotnet test tests/Crm.Kernel.UnitTests tests/Crm.Api.UnitTests` → compile errors (`Unavailable`, `NewId(DateTime)` missing).

- [ ] **Step 2: Kernel**

`Error.cs`: add `Unavailable,` to `ErrorKind` directly before `Unexpected`, and the factory:

```csharp
    public static Error Unavailable(string code, string message) => new(ErrorKind.Unavailable, code, message);
```

`Ids/IIdGenerator.cs`:

```csharp
namespace Crm.Kernel.Ids;

public interface IIdGenerator
{
    Guid NewId();

    /// <summary>UUIDv7 whose time component is <paramref name="utcTimestamp"/> rather than the system clock, so callers that own an IClock get ids that sort by their own notion of time.</summary>
    Guid NewId(DateTime utcTimestamp);
}

public sealed class UuidV7IdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();

    public Guid NewId(DateTime utcTimestamp)
        => Guid.CreateVersion7(new DateTimeOffset(DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc)));
}
```

Run `grep -rn "IIdGenerator" --include="*.cs" src tests | grep -v /obj/`; any other implementer (test fakes) must gain the new member.

- [ ] **Step 3: Host**

`ResultProblemMapper.cs`: add `public const int RetryAfterSeconds = 5;`; in `StatusFor` add `ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,`; in `KindSlug` add `ErrorKind.Unavailable => "unavailable",`; in `TitleFor` add `ErrorKind.Unavailable => "The service is temporarily unavailable.",`.

`ErrorResultFilter.cs`: before the `return Results.Problem(...)`:

```csharp
        if (error.Kind == ErrorKind.Unavailable)
        {
            http.Response.Headers.RetryAfter = ResultProblemMapper.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }
```

`UnhandledExceptionHandler.cs`: after the client-abort guard and before the logging of the unhandled exception, add a branch for `BadHttpRequestException bad`: set `httpContext.Response.StatusCode = bad.StatusCode`, write a `ProblemDetails` with `Status = bad.StatusCode`, `Type = ResultProblemMapper.TypeFor("validation")`, `Title = "The request could not be read."`, `Detail = bad.Message`, `Instance = httpContext.Request.Path` through the same `problems.TryWriteAsync(...)` call shape the existing code uses, and return its result. Do not log it as an error (a `Debug` log through the class's existing `LoggerMessage` pattern is fine). `ProblemDefaults.Apply` already adds `code` (`request.invalid`) and `traceId`.

- [ ] **Step 4: Verify and commit**

Run: `dotnet build Crm.sln -warnaserror` → 0 warnings. `dotnet test Crm.sln --filter "Category!=Integration"` → all green.

```bash
git add src/Shared/Crm.Kernel src/Hosts/Crm.Api/Problems tests/Crm.Kernel.UnitTests tests/Crm.Api.UnitTests
git commit -m "feat(kernel): Unavailable errors map to 503 with Retry-After, timestamped UUIDv7 ids, bad requests are 400 problems

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Request DTOs and validators (UT-001 … UT-005)

**Files:**
- Modify: `Directory.Packages.props` (add `<PackageVersion Include="FluentValidation" Version="12.1.1" />`, keep the file's alphabetical grouping), `src/Modules/Ingestion/Ingestion.Application/Ingestion.Application.csproj` (add `<ItemGroup><PackageReference Include="FluentValidation" /></ItemGroup>`)
- Create under `src/Modules/Ingestion/Ingestion.Application/`: `Requests/TrackRequest.cs`, `Requests/IdentifyRequest.cs`, `Validation/IngestRules.cs`, `Validation/TrackRequestValidator.cs`, `Validation/IdentifyRequestValidator.cs`, `Validation/ValidationFields.cs`
- Test: `tests/Ingestion.UnitTests/FakeClock.cs`, `TrackValidatorTests.cs`, `IdentifyValidatorTests.cs`

**Interfaces:**
- Consumes: `Crm.Kernel.Time.IClock`.
- Produces (namespace `Crm.Ingestion.Application.Requests`): `TrackRequest(string? UserId, string? AnonymousId, string? Event, JsonElement? Properties, DateTimeOffset? Timestamp)`, `IdentifyRequest(string? UserId, string? AnonymousId, JsonElement? Traits)`. Namespace `Crm.Ingestion.Application.Validation`: `IngestRules` (constants + predicates), `TrackRequestValidator(IClock)`, `IdentifyRequestValidator()`, `ValidationFields.From(ValidationResult result, string prefix = "")` → `IReadOnlyDictionary<string, string[]>` keyed by camelCase field path.

- [ ] **Step 1: Failing tests**

`tests/Ingestion.UnitTests/FakeClock.cs`:

```csharp
using Crm.Kernel.Time;

namespace Crm.Ingestion.UnitTests;

internal sealed class FakeClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;
}
```

`tests/Ingestion.UnitTests/TrackValidatorTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using FluentAssertions;
using Xunit;

namespace Crm.Ingestion.UnitTests;

public class TrackValidatorTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    private readonly TrackRequestValidator _validator = new(new FakeClock(Now));

    private static TrackRequest Valid(string? userId = "u1", string? anonymousId = null, string? @event = "purchase", JsonElement? properties = null, DateTimeOffset? timestamp = null)
        => new(userId, anonymousId, @event, properties, timestamp);

    private IReadOnlyDictionary<string, string[]> Errors(TrackRequest request) => ValidationFields.From(_validator.Validate(request));

    [Fact]
    public void UT001_track_requires_userId_or_anonymousId()
    {
        Errors(Valid(userId: null, anonymousId: null)).Should().ContainKey("userId")
            .WhoseValue.Should().Equal("userId or anonymousId is required");
        Errors(Valid(userId: "   ", anonymousId: "")).Should().ContainKey("userId");
        Errors(Valid(userId: "u1", anonymousId: null)).Should().BeEmpty();
        Errors(Valid(userId: null, anonymousId: "a1")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("purchase", true)]
    [InlineData("a_1", true)]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", true)]   // 64
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", false)] // 65
    [InlineData("bad name!", false)]
    [InlineData("émoji", false)]
    [InlineData("purchase\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void UT002_event_name_pattern_and_length(string? name, bool valid)
    {
        var errors = Errors(Valid(@event: name));

        if (valid)
        {
            errors.Should().BeEmpty();
        }
        else
        {
            errors.Should().ContainKey("event").WhoseValue.Should().Equal("must match [A-Za-z0-9_]{1,64}");
        }
    }

    [Fact]
    public void UT003_properties_size_limit_is_32_kb_of_utf8()
    {
        Errors(Valid(properties: ObjectOfBytes(32_768))).Should().BeEmpty();
        Errors(Valid(properties: ObjectOfBytes(32_769))).Should().ContainKey("properties")
            .WhoseValue.Should().Equal("must be at most 32 KB");
    }

    [Fact]
    public void Properties_must_be_an_object_when_present()
    {
        Errors(Valid(properties: Json("[1,2]"))).Should().ContainKey("properties")
            .WhoseValue.Should().Equal("must be a JSON object");
        Errors(Valid(properties: Json("null"))).Should().BeEmpty();
        Errors(Valid(properties: Json("{}"))).Should().BeEmpty();
    }

    [Fact]
    public void UT004_timestamp_window_is_seven_days_either_side_inclusive()
    {
        var now = new DateTimeOffset(Now);

        Errors(Valid(timestamp: now.AddDays(-7))).Should().BeEmpty();
        Errors(Valid(timestamp: now.AddDays(7))).Should().BeEmpty();
        Errors(Valid(timestamp: now.AddDays(-7).AddSeconds(-1))).Should().ContainKey("timestamp");
        Errors(Valid(timestamp: now.AddDays(7).AddSeconds(1))).Should().ContainKey("timestamp")
            .WhoseValue.Should().Equal("must be within 7 days of now");
        Errors(Valid(timestamp: null)).Should().BeEmpty();
    }

    [Fact]
    public void Identifier_lengths_are_bounded_by_the_columns_they_end_up_in()
    {
        Errors(Valid(userId: new string('u', 128))).Should().BeEmpty();
        Errors(Valid(userId: new string('u', 129))).Should().ContainKey("userId")
            .WhoseValue.Should().Equal("must be at most 128 characters");
        Errors(Valid(userId: null, anonymousId: new string('a', 257))).Should().ContainKey("anonymousId")
            .WhoseValue.Should().Equal("must be at most 256 characters");
    }

    /// <summary>A JSON object whose UTF-8 text is exactly <paramref name="bytes"/> bytes: {"k":"aaa…"} has 8 bytes of punctuation.</summary>
    private static JsonElement ObjectOfBytes(int bytes)
    {
        var text = "{\"k\":\"" + new string('a', bytes - 8) + "\"}";
        Encoding.UTF8.GetByteCount(text).Should().Be(bytes);
        return Json(text);
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
```

`tests/Ingestion.UnitTests/IdentifyValidatorTests.cs`:

```csharp
using System.Text.Json;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using FluentAssertions;
using Xunit;

namespace Crm.Ingestion.UnitTests;

public class IdentifyValidatorTests
{
    private readonly IdentifyRequestValidator _validator = new();

    private IReadOnlyDictionary<string, string[]> Errors(IdentifyRequest request) => ValidationFields.From(_validator.Validate(request));

    [Fact]
    public void UT005_identify_requires_userId_and_nothing_else()
    {
        Errors(new IdentifyRequest(null, "a1", null)).Should().ContainKey("userId").WhoseValue.Should().Equal("is required");
        Errors(new IdentifyRequest(" ", null, null)).Should().ContainKey("userId");
        Errors(new IdentifyRequest("u1", null, null)).Should().BeEmpty();
        Errors(new IdentifyRequest("u1", "a1", Json("{\"plan\":\"pro\"}"))).Should().BeEmpty();
    }

    [Fact]
    public void Traits_follow_the_same_shape_and_size_rules_as_properties()
    {
        Errors(new IdentifyRequest("u1", null, Json("\"pro\""))).Should().ContainKey("traits")
            .WhoseValue.Should().Equal("must be a JSON object");
        var big = Json("{\"k\":\"" + new string('a', 32_768) + "\"}");
        Errors(new IdentifyRequest("u1", null, big)).Should().ContainKey("traits")
            .WhoseValue.Should().Equal("must be at most 32 KB");
    }

    [Fact]
    public void Paths_can_be_prefixed_for_batch_items()
    {
        ValidationFields.From(_validator.Validate(new IdentifyRequest(null, null, null)), "events[3].")
            .Should().ContainKey("events[3].userId");
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
```

Run: `dotnet test tests/Ingestion.UnitTests` → compile errors (types missing).

- [ ] **Step 2: DTOs**

`Requests/TrackRequest.cs`:

```csharp
using System.Text.Json;

namespace Crm.Ingestion.Application.Requests;

/// <summary>Body of POST /v1/track. Everything is nullable so a missing field reaches the validator instead of failing deserialization.</summary>
public sealed record TrackRequest(string? UserId, string? AnonymousId, string? Event, JsonElement? Properties, DateTimeOffset? Timestamp);
```

`Requests/IdentifyRequest.cs`:

```csharp
using System.Text.Json;

namespace Crm.Ingestion.Application.Requests;

/// <summary>Body of POST /v1/identify.</summary>
public sealed record IdentifyRequest(string? UserId, string? AnonymousId, JsonElement? Traits);
```

- [ ] **Step 3: Rules and validators**

`Validation/IngestRules.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Crm.Ingestion.Application.Validation;

/// <summary>The limits of the ingest contract (spec §4.1, docs/api/README.md), in one place.</summary>
public static partial class IngestRules
{
    public const int MaxJsonBytes = 32 * 1024;
    public const int MaxUserIdLength = 128;       // profiles.Profiles.ExternalId
    public const int MaxAnonymousIdLength = 256;  // profiles.UserIdentities.Value
    public const int MaxBatchItems = 500;
    public static readonly TimeSpan TimestampWindow = TimeSpan.FromDays(7);

    public const string EventNameMessage = "must match [A-Za-z0-9_]{1,64}";
    public const string ObjectMessage = "must be a JSON object";
    public const string SizeMessage = "must be at most 32 KB";

    // \z, not $: `$` also matches before a trailing newline.
    [GeneratedRegex(@"^[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EventName();

    public static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    public static bool IsValidEventName(string? value) => value is not null && EventName().IsMatch(value);

    public static bool IsObjectOrAbsent(JsonElement? element)
        => element is null || element.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined;

    /// <summary>Size of the JSON text as received, in UTF-8 bytes. Non-objects are the other rule's business.</summary>
    public static bool FitsSizeLimit(JsonElement? element)
        => element is null
           || element.Value.ValueKind is not JsonValueKind.Object
           || Encoding.UTF8.GetByteCount(element.Value.GetRawText()) <= MaxJsonBytes;

    public static bool IsWithinWindow(DateTimeOffset? timestamp, DateTime utcNow)
        => timestamp is null || (timestamp.Value.UtcDateTime - utcNow).Duration() <= TimestampWindow;
}
```

`Validation/TrackRequestValidator.cs`:

```csharp
using Crm.Ingestion.Application.Requests;
using Crm.Kernel.Time;
using FluentValidation;

namespace Crm.Ingestion.Application.Validation;

public sealed class TrackRequestValidator : AbstractValidator<TrackRequest>
{
    public TrackRequestValidator(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(x => x.UserId)
            .Must((request, _) => !IngestRules.IsBlank(request.UserId) || !IngestRules.IsBlank(request.AnonymousId))
            .OverridePropertyName("userId").WithMessage("userId or anonymousId is required");
        RuleFor(x => x.UserId).MaximumLength(IngestRules.MaxUserIdLength)
            .OverridePropertyName("userId").WithMessage("must be at most 128 characters");
        RuleFor(x => x.AnonymousId).MaximumLength(IngestRules.MaxAnonymousIdLength)
            .OverridePropertyName("anonymousId").WithMessage("must be at most 256 characters");
        RuleFor(x => x.Event).Must(IngestRules.IsValidEventName)
            .OverridePropertyName("event").WithMessage(IngestRules.EventNameMessage);
        RuleFor(x => x.Properties).Must(IngestRules.IsObjectOrAbsent)
            .OverridePropertyName("properties").WithMessage(IngestRules.ObjectMessage);
        RuleFor(x => x.Properties).Must(IngestRules.FitsSizeLimit)
            .OverridePropertyName("properties").WithMessage(IngestRules.SizeMessage);
        RuleFor(x => x.Timestamp).Must(timestamp => IngestRules.IsWithinWindow(timestamp, clock.UtcNow))
            .OverridePropertyName("timestamp").WithMessage("must be within 7 days of now");
    }
}
```

`Validation/IdentifyRequestValidator.cs`:

```csharp
using Crm.Ingestion.Application.Requests;
using FluentValidation;

namespace Crm.Ingestion.Application.Validation;

public sealed class IdentifyRequestValidator : AbstractValidator<IdentifyRequest>
{
    public IdentifyRequestValidator()
    {
        RuleFor(x => x.UserId).Must(userId => !IngestRules.IsBlank(userId))
            .OverridePropertyName("userId").WithMessage("is required");
        RuleFor(x => x.UserId).MaximumLength(IngestRules.MaxUserIdLength)
            .OverridePropertyName("userId").WithMessage("must be at most 128 characters");
        RuleFor(x => x.AnonymousId).MaximumLength(IngestRules.MaxAnonymousIdLength)
            .OverridePropertyName("anonymousId").WithMessage("must be at most 256 characters");
        RuleFor(x => x.Traits).Must(IngestRules.IsObjectOrAbsent)
            .OverridePropertyName("traits").WithMessage(IngestRules.ObjectMessage);
        RuleFor(x => x.Traits).Must(IngestRules.FitsSizeLimit)
            .OverridePropertyName("traits").WithMessage(IngestRules.SizeMessage);
    }
}
```

`Validation/ValidationFields.cs`:

```csharp
using FluentValidation.Results;

namespace Crm.Ingestion.Application.Validation;

public static class ValidationFields
{
    /// <summary>FluentValidation failures → the `errors` object of a problem response: field path → messages.</summary>
    public static IReadOnlyDictionary<string, string[]> From(ValidationResult result, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Errors
            .GroupBy(failure => prefix + failure.PropertyName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(f => f.ErrorMessage).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }
}
```

- [ ] **Step 4: Verify and commit**

Run: `dotnet test tests/Ingestion.UnitTests` → all pass. `dotnet build Crm.sln -warnaserror` → 0 warnings. `dotnet test tests/Crm.ArchTests` → green (AT-001/AT-003 still hold with FluentValidation).

```bash
git add Directory.Packages.props src/Modules/Ingestion/Ingestion.Application tests/Ingestion.UnitTests
git commit -m "feat(ingestion): track and identify requests with their validation rules (UT-001..005)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Envelope factory — ids, partition key, payload, headers (UT-007, UT-008)

**Files:**
- Create: `src/Modules/Ingestion/Ingestion.Application/Envelopes/RawMessage.cs`, `Envelopes/EventEnvelopeFactory.cs`
- Test: `tests/Ingestion.UnitTests/EventEnvelopeTests.cs`

**Interfaces:**
- Consumes: `TrackRequest`, `IdentifyRequest` (Task 2); `IIdGenerator.NewId(DateTime)` (Task 1); `IClock`; `Crm.Contracts.{TrackEventRaw, IdentifyRaw, ContractJson, KafkaHeaders}`.
- Produces (namespace `Crm.Ingestion.Application.Envelopes`): `RawMessage(Guid EventId, string MessageType, string Key, byte[] Value, IReadOnlyDictionary<string, string> Headers)`; `EventEnvelopeFactory(IIdGenerator ids, IClock clock)` with `RawMessage Track(Guid tenantId, TrackRequest request)`, `RawMessage Identify(Guid tenantId, IdentifyRequest request)`, `static string PartitionKey(Guid tenantId, string? userId, string? anonymousId)`. Requests are assumed valid (the service validates first).

- [ ] **Step 1: Failing tests**

`tests/Ingestion.UnitTests/EventEnvelopeTests.cs`:

```csharp
using System.Text.Json;
using Crm.Contracts;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Application.Requests;
using Crm.Kernel.Ids;
using FluentAssertions;
using Xunit;

namespace Crm.Ingestion.UnitTests;

public class EventEnvelopeTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void UT007_event_ids_are_uuid_v7_and_time_ordered()
    {
        var clock = new FakeClock(Now);
        var factory = new EventEnvelopeFactory(new UuidV7IdGenerator(), clock);

        var first = factory.Track(Tenant, new TrackRequest("u1", null, "purchase", null, null));
        clock.UtcNow = Now.AddMilliseconds(1);
        var second = factory.Track(Tenant, new TrackRequest("u1", null, "purchase", null, null));

        first.EventId.Version.Should().Be(7);
        second.EventId.Version.Should().Be(7);
        second.EventId.CompareTo(first.EventId).Should().BePositive();
        string.CompareOrdinal(second.EventId.ToString(), first.EventId.ToString()).Should().BePositive();
    }

    [Fact]
    public void UT008_kafka_key_is_tenant_plus_user_key()
    {
        EventEnvelopeFactory.PartitionKey(Tenant, "u1", "a1").Should().Be("11111111222233334444555555555555:u1");
        EventEnvelopeFactory.PartitionKey(Tenant, null, "a1").Should().Be("11111111222233334444555555555555:a1");
        EventEnvelopeFactory.PartitionKey(Tenant, "  ", "a1").Should().Be("11111111222233334444555555555555:a1");
    }

    [Fact]
    public void Track_payload_is_the_contract_message_with_defaults_applied()
    {
        var factory = new EventEnvelopeFactory(new UuidV7IdGenerator(), new FakeClock(Now));

        var message = factory.Track(Tenant, new TrackRequest("u1", " ", "purchase", null, null));
        var payload = JsonSerializer.Deserialize<TrackEventRaw>(message.Value, ContractJson.Options)!;

        message.MessageType.Should().Be(nameof(TrackEventRaw));
        message.Key.Should().Be("11111111222233334444555555555555:u1");
        payload.EventId.Should().Be(message.EventId);
        payload.TenantId.Should().Be(Tenant);
        payload.UserId.Should().Be("u1");
        payload.AnonymousId.Should().BeNull("blank identifiers count as absent");
        payload.Event.Should().Be("purchase");
        payload.Properties.ValueKind.Should().Be(JsonValueKind.Object);
        payload.Properties.EnumerateObject().Should().BeEmpty("absent properties become {}");
        payload.OccurredAt.Should().Be(Now, "an absent timestamp resolves to the clock (UT-004)");
        payload.ReceivedAt.Should().Be(Now);
    }

    [Fact]
    public void A_client_timestamp_becomes_OccurredAt_in_utc()
    {
        var factory = new EventEnvelopeFactory(new UuidV7IdGenerator(), new FakeClock(Now));
        var clientTime = new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.FromHours(7));

        var message = factory.Track(Tenant, new TrackRequest("u1", null, "purchase", null, clientTime));
        var payload = JsonSerializer.Deserialize<TrackEventRaw>(message.Value, ContractJson.Options)!;

        payload.OccurredAt.Should().Be(new DateTime(2026, 9, 15, 3, 0, 0, DateTimeKind.Utc));
        payload.ReceivedAt.Should().Be(Now);
    }

    [Fact]
    public void Identify_payload_and_headers()
    {
        var factory = new EventEnvelopeFactory(new UuidV7IdGenerator(), new FakeClock(Now));
        using var traits = JsonDocument.Parse("{\"plan\":\"pro\"}");

        var message = factory.Identify(Tenant, new IdentifyRequest("u2", "a9", traits.RootElement.Clone()));
        var payload = JsonSerializer.Deserialize<IdentifyRaw>(message.Value, ContractJson.Options)!;

        message.MessageType.Should().Be(nameof(IdentifyRaw));
        message.Key.Should().Be("11111111222233334444555555555555:u2");
        payload.UserId.Should().Be("u2");
        payload.AnonymousId.Should().Be("a9");
        payload.Traits.GetProperty("plan").GetString().Should().Be("pro");
        message.Headers.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [KafkaHeaders.TenantId] = "11111111222233334444555555555555",
            [KafkaHeaders.MessageType] = nameof(IdentifyRaw),
            [KafkaHeaders.ContentType] = KafkaHeaders.JsonContentType,
        });
    }
}
```

Run: `dotnet test tests/Ingestion.UnitTests --filter "FullyQualifiedName~EventEnvelopeTests"` → compile errors.

- [ ] **Step 2: Implementation**

`Envelopes/RawMessage.cs`:

```csharp
namespace Crm.Ingestion.Application.Envelopes;

/// <summary>One record for events.raw, ready to hand to a producer: nothing Kafka-specific leaks into the application layer.</summary>
public sealed record RawMessage(Guid EventId, string MessageType, string Key, byte[] Value, IReadOnlyDictionary<string, string> Headers);
```

`Envelopes/EventEnvelopeFactory.cs`:

```csharp
using System.Text.Json;
using Crm.Contracts;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using Crm.Kernel.Ids;
using Crm.Kernel.Time;

namespace Crm.Ingestion.Application.Envelopes;

/// <summary>Validated request → events.raw record: assigns the event id, stamps tenant and receive time, picks the partition key.</summary>
public sealed class EventEnvelopeFactory(IIdGenerator ids, IClock clock)
{
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new Dictionary<string, string>());

    /// <summary>{tenant:N}:{userKey}; userKey is userId when present, else anonymousId, so one person's events share a partition.</summary>
    public static string PartitionKey(Guid tenantId, string? userId, string? anonymousId)
        => $"{tenantId:N}:{(IngestRules.IsBlank(userId) ? anonymousId : userId)}";

    public RawMessage Track(Guid tenantId, TrackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        var payload = new TrackEventRaw(
            ids.NewId(now),
            tenantId,
            NullIfBlank(request.UserId),
            NullIfBlank(request.AnonymousId),
            request.Event!,
            ObjectOrEmpty(request.Properties),
            request.Timestamp?.UtcDateTime ?? now,
            now);
        return Build(payload.EventId, nameof(TrackEventRaw), tenantId, PartitionKey(tenantId, payload.UserId, payload.AnonymousId), payload);
    }

    public RawMessage Identify(Guid tenantId, IdentifyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        var payload = new IdentifyRaw(
            ids.NewId(now),
            tenantId,
            request.UserId!,
            NullIfBlank(request.AnonymousId),
            ObjectOrEmpty(request.Traits),
            now);
        return Build(payload.EventId, nameof(IdentifyRaw), tenantId, PartitionKey(tenantId, payload.UserId, payload.AnonymousId), payload);
    }

    private static RawMessage Build<T>(Guid eventId, string messageType, Guid tenantId, string key, T payload)
        => new(eventId, messageType, key, JsonSerializer.SerializeToUtf8Bytes(payload, ContractJson.Options), new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KafkaHeaders.TenantId] = tenantId.ToString("N"),
            [KafkaHeaders.MessageType] = messageType,
            [KafkaHeaders.ContentType] = KafkaHeaders.JsonContentType,
        });

    private static string? NullIfBlank(string? value) => IngestRules.IsBlank(value) ? null : value;

    private static JsonElement ObjectOrEmpty(JsonElement? element)
        => element is { ValueKind: JsonValueKind.Object } value ? value : EmptyObject;
}
```

- [ ] **Step 3: Verify and commit**

Run: `dotnet test tests/Ingestion.UnitTests` → all pass; `dotnet build Crm.sln -warnaserror` → 0 warnings.

```bash
git add src/Modules/Ingestion/Ingestion.Application/Envelopes tests/Ingestion.UnitTests/EventEnvelopeTests.cs
git commit -m "feat(ingestion): envelope factory assigns UUIDv7 ids, tenant-scoped partition keys and contract payloads (UT-007, UT-008)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: `IngestService`, batch semantics, publisher port, metrics (UT-006)

**Files:**
- Create under `src/Modules/Ingestion/Ingestion.Application/`: `Requests/BatchRequest.cs`, `Requests/IngestResponses.cs`, `IEventPublisher.cs`, `EventPublishException.cs`, `IngestErrors.cs`, `IngestMetrics.cs`, `IngestService.cs`
- Test: `tests/Ingestion.UnitTests/RecordingPublisher.cs`, `BatchValidatorTests.cs`, `IngestServiceTests.cs`

**Interfaces:**
- Consumes: validators and `ValidationFields` (Task 2), `EventEnvelopeFactory`/`RawMessage` (Task 3), `Error.Unavailable` (Task 1), `Crm.Kernel.Result<T>`.
- Produces:
  - `BatchItem(string? Type, string? UserId, string? AnonymousId, string? Event, JsonElement? Properties, JsonElement? Traits, DateTimeOffset? Timestamp)`, `BatchRequest(IReadOnlyList<BatchItem?>? Events)`
  - `IngestAccepted(Guid EventId)`, `BatchItemResult(int Index, Guid? EventId, IReadOnlyDictionary<string, string[]>? Errors)` (null members omitted from JSON), `BatchAccepted(int Accepted, int Rejected, IReadOnlyList<BatchItemResult> Results)`
  - `IEventPublisher { Task PublishAsync(RawMessage message, CancellationToken cancellationToken); }` — throws `EventPublishException` when the broker did not acknowledge
  - `IngestService` with `Task<Result<IngestAccepted>> TrackAsync(Guid tenantId, TrackRequest? request, CancellationToken ct)`, `IdentifyAsync(Guid, IdentifyRequest?, CancellationToken)`, `Task<Result<BatchAccepted>> BatchAsync(Guid, BatchRequest?, CancellationToken)`
  - `IngestMetrics` (`MeterName = "Crm.Ingestion"`, `Accepted(string type, long count = 1)`, `Rejected(string reason, long count = 1)`, `IDisposable`)

- [ ] **Step 1: Failing tests**

`tests/Ingestion.UnitTests/RecordingPublisher.cs`:

```csharp
using System.Collections.Concurrent;
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;

namespace Crm.Ingestion.UnitTests;

internal sealed class RecordingPublisher : IEventPublisher
{
    public ConcurrentQueue<RawMessage> Published { get; } = new();

    public bool Fail { get; set; }

    public Task PublishAsync(RawMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new EventPublishException("broker down", new TimeoutException());
        }

        Published.Enqueue(message);
        return Task.CompletedTask;
    }
}
```

`tests/Ingestion.UnitTests/BatchValidatorTests.cs`:

```csharp
using Crm.Contracts;
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using Crm.Kernel;
using Crm.Kernel.Ids;
using FluentAssertions;
using Xunit;

namespace Crm.Ingestion.UnitTests;

// Same collection as IngestServiceTests: a MeterListener is process-wide, so classes that record on the
// Crm.Ingestion meter must not run in parallel with the one that listens to it.
[Collection("IngestMetrics")]
public class BatchValidatorTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly RecordingPublisher _publisher = new();
    private readonly IngestService _service;

    public BatchValidatorTests()
    {
        var clock = new FakeClock(Now);
        _service = new IngestService(
            new TrackRequestValidator(clock), new IdentifyRequestValidator(),
            new EventEnvelopeFactory(new UuidV7IdGenerator(), clock), _publisher, new IngestMetrics());
    }

    private static BatchItem Track(string @event = "app_open") => new("track", "u1", null, @event, null, null, null);

    [Fact]
    public async Task UT006_batch_of_500_passes_and_501_fails()
    {
        var ok = await _service.BatchAsync(Tenant, new BatchRequest(Enumerable.Repeat(Track(), 500).ToList()), default);
        ok.IsSuccess.Should().BeTrue();
        ok.Value.Accepted.Should().Be(500);

        var tooMany = await _service.BatchAsync(Tenant, new BatchRequest(Enumerable.Repeat(Track(), 501).ToList()), default);
        tooMany.Error!.Kind.Should().Be(ErrorKind.Validation);
        tooMany.Error.Fields.Should().ContainKey("events").WhoseValue.Should().Equal("at most 500 items");
        _publisher.Published.Should().HaveCount(500, "a rejected envelope publishes nothing");
    }

    [Fact]
    public async Task UT006_items_are_validated_independently()
    {
        var batch = new BatchRequest([Track(), Track("bad name!"), new BatchItem("identify", "u2", null, null, null, null, null)]);

        var result = await _service.BatchAsync(Tenant, batch, default);

        result.Value.Accepted.Should().Be(2);
        result.Value.Rejected.Should().Be(1);
        result.Value.Results.Select(r => r.Index).Should().Equal(0, 1, 2);
        result.Value.Results[0].EventId.Should().NotBeNull();
        result.Value.Results[1].EventId.Should().BeNull();
        result.Value.Results[1].Errors.Should().ContainKey("events[1].event").WhoseValue.Should().Equal("must match [A-Za-z0-9_]{1,64}");
        result.Value.Results[2].EventId.Should().NotBeNull();
        _publisher.Published.Select(m => m.MessageType).Should().BeEquivalentTo([nameof(TrackEventRaw), nameof(IdentifyRaw)]);
        _publisher.Published.Select(m => m.EventId).Should().BeEquivalentTo(new[] { result.Value.Results[0].EventId!.Value, result.Value.Results[2].EventId!.Value });
    }

    [Fact]
    public async Task Missing_empty_or_null_envelopes_are_rejected()
    {
        (await _service.BatchAsync(Tenant, null, default)).Error!.Fields.Should().ContainKey("events");
        (await _service.BatchAsync(Tenant, new BatchRequest(null), default)).Error!.Fields.Should().ContainKey("events")
            .WhoseValue.Should().Equal("at least 1 item");
        (await _service.BatchAsync(Tenant, new BatchRequest([]), default)).Error!.Fields.Should().ContainKey("events");
    }

    [Fact]
    public async Task Unknown_type_and_null_items_are_item_errors()
    {
        var batch = new BatchRequest([new BatchItem("page", "u1", null, "x", null, null, null), null, new BatchItem(null, "u1", null, "x", null, null, null)]);

        var result = await _service.BatchAsync(Tenant, batch, default);

        result.Value.Accepted.Should().Be(0);
        result.Value.Rejected.Should().Be(3);
        result.Value.Results[0].Errors.Should().ContainKey("events[0].type").WhoseValue.Should().Equal("must be track or identify");
        result.Value.Results[1].Errors.Should().ContainKey("events[1]").WhoseValue.Should().Equal("must be an object");
        result.Value.Results[2].Errors.Should().ContainKey("events[2].type");
        _publisher.Published.Should().BeEmpty();
    }
}
```

`tests/Ingestion.UnitTests/IngestServiceTests.cs`:

```csharp
using System.Diagnostics.Metrics;
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using Crm.Kernel;
using Crm.Kernel.Ids;
using FluentAssertions;
using Xunit;

namespace Crm.Ingestion.UnitTests;

[Collection("IngestMetrics")]
public sealed class IngestServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly RecordingPublisher _publisher = new();
    private readonly IngestMetrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, long Value, string? Tag)> _measurements = [];
    private readonly IngestService _service;

    public IngestServiceTests()
    {
        var clock = new FakeClock(Now);
        _service = new IngestService(
            new TrackRequestValidator(clock), new IdentifyRequestValidator(),
            new EventEnvelopeFactory(new UuidV7IdGenerator(), clock), _publisher, _metrics);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == IngestMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (_measurements)
            {
                _measurements.Add((instrument.Name, value, tags.Length > 0 ? tags[0].Value?.ToString() : null));
            }
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public async Task A_valid_track_is_published_and_counted()
    {
        var result = await _service.TrackAsync(Tenant, new TrackRequest("u1", null, "purchase", null, null), default);

        result.IsSuccess.Should().BeTrue();
        _publisher.Published.Should().ContainSingle().Which.EventId.Should().Be(result.Value.EventId);
        _measurements.Should().ContainSingle(m => m.Tag == "track" && m.Value == 1);
    }

    [Fact]
    public async Task An_invalid_track_is_a_validation_error_naming_the_first_field_and_is_counted_as_rejected()
    {
        var result = await _service.TrackAsync(Tenant, new TrackRequest("u1", null, "bad name!", null, null), default);

        result.Error!.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Code.Should().Be("ingest.invalid");
        result.Error.Message.Should().Be("event must match [A-Za-z0-9_]{1,64}");
        result.Error.Fields.Should().ContainKey("event");
        _publisher.Published.Should().BeEmpty();
        _measurements.Should().ContainSingle(m => m.Tag == "validation" && m.Value == 1);
    }

    [Fact]
    public async Task A_missing_body_is_a_validation_error()
    {
        (await _service.TrackAsync(Tenant, null, default)).Error!.Fields.Should().ContainKey("body").WhoseValue.Should().Equal("is required");
        (await _service.IdentifyAsync(Tenant, null, default)).Error!.Fields.Should().ContainKey("body");
    }

    [Fact]
    public async Task A_broker_failure_is_Unavailable_and_nothing_is_counted_as_accepted()
    {
        _publisher.Fail = true;

        var track = await _service.TrackAsync(Tenant, new TrackRequest("u1", null, "purchase", null, null), default);
        var batch = await _service.BatchAsync(Tenant, new BatchRequest([new BatchItem("identify", "u1", null, null, null, null, null)]), default);

        track.Error!.Kind.Should().Be(ErrorKind.Unavailable);
        track.Error.Code.Should().Be("ingest.unavailable");
        batch.Error!.Kind.Should().Be(ErrorKind.Unavailable);
        _measurements.Should().NotContain(m => m.Tag == "track" || m.Tag == "identify");
        _measurements.Where(m => m.Tag == "unavailable").Sum(m => m.Value).Should().Be(2);
    }

    [Fact]
    public async Task Identify_is_published_with_its_own_type()
    {
        var result = await _service.IdentifyAsync(Tenant, new IdentifyRequest("u2", "a1", null), default);

        result.IsSuccess.Should().BeTrue();
        _publisher.Published.Should().ContainSingle().Which.MessageType.Should().Be("IdentifyRaw");
        _measurements.Should().ContainSingle(m => m.Tag == "identify");
    }
}
```

Run: `dotnet test tests/Ingestion.UnitTests` → compile errors.

- [ ] **Step 2: DTOs and port**

`Requests/BatchRequest.cs`:

```csharp
using System.Text.Json;

namespace Crm.Ingestion.Application.Requests;

/// <summary>One flat shape for both item kinds; `type` ("track" | "identify") selects which fields and rules apply.</summary>
public sealed record BatchItem(string? Type, string? UserId, string? AnonymousId, string? Event, JsonElement? Properties, JsonElement? Traits, DateTimeOffset? Timestamp)
{
    public TrackRequest ToTrack() => new(UserId, AnonymousId, Event, Properties, Timestamp);

    public IdentifyRequest ToIdentify() => new(UserId, AnonymousId, Traits);
}

/// <summary>Body of POST /v1/batch.</summary>
public sealed record BatchRequest(IReadOnlyList<BatchItem?>? Events);
```

`Requests/IngestResponses.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Crm.Ingestion.Application.Requests;

public sealed record IngestAccepted(Guid EventId);

/// <summary>Exactly one of EventId (accepted) or Errors (rejected) is present.</summary>
public sealed record BatchItemResult(
    int Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? EventId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string[]>? Errors);

public sealed record BatchAccepted(int Accepted, int Rejected, IReadOnlyList<BatchItemResult> Results);
```

`IEventPublisher.cs`:

```csharp
using Crm.Ingestion.Application.Envelopes;

namespace Crm.Ingestion.Application;

public interface IEventPublisher
{
    /// <summary>Completes only after the broker acknowledged the record (acks=all). Throws <see cref="EventPublishException"/> otherwise; nothing is kept for a later retry.</summary>
    Task PublishAsync(RawMessage message, CancellationToken cancellationToken);
}
```

`EventPublishException.cs`:

```csharp
namespace Crm.Ingestion.Application;

public sealed class EventPublishException : Exception
{
    public EventPublishException()
    {
    }

    public EventPublishException(string message)
        : base(message)
    {
    }

    public EventPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

`IngestErrors.cs`:

```csharp
using Crm.Kernel;

namespace Crm.Ingestion.Application;

public static class IngestErrors
{
    public const string InvalidCode = "ingest.invalid";
    public const string UnavailableCode = "ingest.unavailable";

    /// <summary>`detail` names the first failing field the way docs/api/README.md shows it: "event must match …".</summary>
    public static Error Invalid(IReadOnlyDictionary<string, string[]> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var first = fields.First();
        return Error.Validation(InvalidCode, $"{first.Key} {first.Value[0]}", fields);
    }

    public static Error Invalid(string field, string message)
        => Invalid(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    public static readonly Error Unavailable = Error.Unavailable(UnavailableCode, "The event stream is temporarily unavailable; retry the request.");
}
```

- [ ] **Step 3: Metrics**

`IngestMetrics.cs`:

```csharp
using System.Diagnostics.Metrics;

namespace Crm.Ingestion.Application;

/// <summary>
/// Spec §8 names the Prometheus series crm_ingest_events_total and crm_ingest_rejected_total. The OpenTelemetry
/// Prometheus exporter appends `_total` to counters, so the instruments are named without it.
/// </summary>
public sealed class IngestMetrics : IDisposable
{
    public const string MeterName = "Crm.Ingestion";
    public const string TypeTrack = "track";
    public const string TypeIdentify = "identify";
    public const string ReasonValidation = "validation";
    public const string ReasonUnavailable = "unavailable";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _accepted;
    private readonly Counter<long> _rejected;

    public IngestMetrics()
    {
        _accepted = _meter.CreateCounter<long>("crm_ingest_events", description: "Events acknowledged by Kafka, by type.");
        _rejected = _meter.CreateCounter<long>("crm_ingest_rejected", description: "Events or requests not accepted, by reason.");
    }

    public void Accepted(string type, long count = 1) => _accepted.Add(count, new KeyValuePair<string, object?>("type", type));

    public void Rejected(string reason, long count = 1) => _rejected.Add(count, new KeyValuePair<string, object?>("reason", reason));

    public void Dispose() => _meter.Dispose();
}
```

- [ ] **Step 4: Service**

`IngestService.cs`:

```csharp
using Crm.Contracts;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using Crm.Kernel;
using FluentValidation;

namespace Crm.Ingestion.Application;

/// <summary>validate → envelope → publish. No database, no cache: the only side effect is the Kafka record.</summary>
public sealed class IngestService(
    IValidator<TrackRequest> trackValidator,
    IValidator<IdentifyRequest> identifyValidator,
    EventEnvelopeFactory envelopes,
    IEventPublisher publisher,
    IngestMetrics metrics)
{
    public async Task<Result<IngestAccepted>> TrackAsync(Guid tenantId, TrackRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Reject(IngestErrors.Invalid("body", "is required"));
        }

        var validation = await trackValidator.ValidateAsync(request, cancellationToken);
        return validation.IsValid
            ? await PublishOneAsync(envelopes.Track(tenantId, request), IngestMetrics.TypeTrack, cancellationToken)
            : Reject(IngestErrors.Invalid(ValidationFields.From(validation)));
    }

    public async Task<Result<IngestAccepted>> IdentifyAsync(Guid tenantId, IdentifyRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Reject(IngestErrors.Invalid("body", "is required"));
        }

        var validation = await identifyValidator.ValidateAsync(request, cancellationToken);
        return validation.IsValid
            ? await PublishOneAsync(envelopes.Identify(tenantId, request), IngestMetrics.TypeIdentify, cancellationToken)
            : Reject(IngestErrors.Invalid(ValidationFields.From(validation)));
    }

    public async Task<Result<BatchAccepted>> BatchAsync(Guid tenantId, BatchRequest? request, CancellationToken cancellationToken)
    {
        var items = request?.Events;
        if (items is null || items.Count == 0)
        {
            metrics.Rejected(IngestMetrics.ReasonValidation);
            return IngestErrors.Invalid("events", "at least 1 item");
        }

        if (items.Count > IngestRules.MaxBatchItems)
        {
            metrics.Rejected(IngestMetrics.ReasonValidation);
            return IngestErrors.Invalid("events", "at most 500 items");
        }

        var results = new BatchItemResult[items.Count];
        var accepted = new List<RawMessage>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var (message, errors) = await BuildItemAsync(tenantId, items[index], index, cancellationToken);
            results[index] = new BatchItemResult(index, message?.EventId, errors);
            if (message is not null)
            {
                accepted.Add(message);
            }
        }

        try
        {
            await Task.WhenAll(accepted.Select(message => publisher.PublishAsync(message, cancellationToken)));
        }
        catch (EventPublishException)
        {
            metrics.Rejected(IngestMetrics.ReasonUnavailable, accepted.Count);
            return IngestErrors.Unavailable;
        }

        Count(accepted, nameof(TrackEventRaw), IngestMetrics.TypeTrack);
        Count(accepted, nameof(IdentifyRaw), IngestMetrics.TypeIdentify);
        var rejected = items.Count - accepted.Count;
        if (rejected > 0)
        {
            metrics.Rejected(IngestMetrics.ReasonValidation, rejected);
        }

        return new BatchAccepted(accepted.Count, rejected, results);
    }

    private async Task<(RawMessage? Message, IReadOnlyDictionary<string, string[]>? Errors)> BuildItemAsync(Guid tenantId, BatchItem? item, int index, CancellationToken cancellationToken)
    {
        var prefix = $"events[{index}]";
        if (item is null)
        {
            return (null, Single(prefix, "must be an object"));
        }

        if (string.Equals(item.Type, "track", StringComparison.OrdinalIgnoreCase))
        {
            var track = item.ToTrack();
            var validation = await trackValidator.ValidateAsync(track, cancellationToken);
            return validation.IsValid ? (envelopes.Track(tenantId, track), null) : (null, ValidationFields.From(validation, prefix + "."));
        }

        if (string.Equals(item.Type, "identify", StringComparison.OrdinalIgnoreCase))
        {
            var identify = item.ToIdentify();
            var validation = await identifyValidator.ValidateAsync(identify, cancellationToken);
            return validation.IsValid ? (envelopes.Identify(tenantId, identify), null) : (null, ValidationFields.From(validation, prefix + "."));
        }

        return (null, Single(prefix + ".type", "must be track or identify"));
    }

    private async Task<Result<IngestAccepted>> PublishOneAsync(RawMessage message, string type, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishAsync(message, cancellationToken);
        }
        catch (EventPublishException)
        {
            metrics.Rejected(IngestMetrics.ReasonUnavailable);
            return IngestErrors.Unavailable;
        }

        metrics.Accepted(type);
        return new IngestAccepted(message.EventId);
    }

    private Error Reject(Error error)
    {
        metrics.Rejected(IngestMetrics.ReasonValidation);
        return error;
    }

    private void Count(List<RawMessage> accepted, string messageType, string metricType)
    {
        var count = accepted.Count(m => m.MessageType == messageType);
        if (count > 0)
        {
            metrics.Accepted(metricType, count);
        }
    }

    private static Dictionary<string, string[]> Single(string path, string message)
        => new(StringComparer.Ordinal) { [path] = [message] };
}
```

Note for the implementer: `Reject` returns `Error`, which converts implicitly to `Result<IngestAccepted>`; if the compiler cannot pick the conversion inside the conditional expression, split the expression into an `if`/`return` — do not change behaviour.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test tests/Ingestion.UnitTests` → all pass, output pristine. `dotnet build Crm.sln -warnaserror` → 0 warnings. `dotnet test tests/Crm.ArchTests` → green.

```bash
git add src/Modules/Ingestion/Ingestion.Application tests/Ingestion.UnitTests
git commit -m "feat(ingestion): ingest service with per-item batch results, publisher port and ingest meters (UT-006)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Kafka publisher, endpoints, host wiring, OpenAPI contract

**Files:**
- Modify: `src/Modules/Ingestion/Ingestion.Infrastructure/Ingestion.Infrastructure.csproj` (add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in its own `ItemGroup`, the way `Tenancy.Infrastructure.csproj` does), `IngestionModule.cs`
- Create: `src/Modules/Ingestion/Ingestion.Infrastructure/Kafka/KafkaProducerOptions.cs`, `Kafka/KafkaEventPublisher.cs`, `Endpoints/IngestEndpoints.cs`, `Endpoints/IngestionEndpointExtensions.cs`
- Modify: `src/Hosts/Crm.Api/Program.cs`, `tests/Crm.Api.UnitTests/OpenApiDocumentTests.cs`, `docs/api/openapi.json` (regenerated)

**Interfaces:**
- Consumes: `IngestService`, `IEventPublisher`, `RawMessage`, `IngestMetrics`, validators (Tasks 2–4); `Crm.Kernel.Tenancy.ITenantContext.TenantId`; host constants `AuthPolicies.IngestOnly`, `RateLimitPolicies.Ingest`, `EndpointTags.Ingest` from `Crm.Tenancy.Infrastructure.Auth`.
- Produces: `IngestionEndpointExtensions.MapIngestionEndpoints(this RouteGroupBuilder ingest)`; configuration keys `Kafka:BootstrapServers` (existing), `Kafka:Producer:MessageTimeoutMs` (default 3000), `Kafka:Producer:LingerMs` (default 5).

- [ ] **Step 1: Contract test first**

In `OpenApiDocumentTests.cs`: update the class summary (it is no longer "exactly the Tenancy operations"), rename `CT001_committed_document_lists_exactly_the_tenancy_operations` → `CT001_committed_document_lists_exactly_the_implemented_operations`, add to `Expected` (keep ordinal order): `"POST /v1/batch"`, `"POST /v1/identify"`, `"POST /v1/track"`. Replace the `bool RequiresBearer` member of `ExpectedOperation` with `string? Security` (`"bearer"`, `"apiKey"` or `null`), adapt the existing rows, and add:

```csharp
        new("POST", "/v1/track", ["202", "400", "401", "403", "429", "503"], Security: "apiKey"),
        new("POST", "/v1/identify", ["202", "400", "401", "403", "429", "503"], Security: "apiKey"),
        new("POST", "/v1/batch", ["202", "400", "401", "403", "429", "503"], Security: "apiKey"),
```

The security assertion becomes: when `Security` is not null the operation has exactly one requirement whose single key equals it; when null there is no `security`. Extend the problem-content loop to responses starting with `'4'` **or** `'5'`.

Run: `dotnet test tests/Crm.Api.UnitTests --filter "FullyQualifiedName~OpenApiDocumentTests"` → FAIL (operations missing from the committed document).

- [ ] **Step 2: Producer**

`Kafka/KafkaProducerOptions.cs`:

```csharp
namespace Crm.Ingestion.Infrastructure.Kafka;

public sealed class KafkaProducerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    public ProducerSettings Producer { get; set; } = new();

    public sealed class ProducerSettings
    {
        /// <summary>How long a record may wait for the broker's acknowledgement before the request fails with 503.</summary>
        public int MessageTimeoutMs { get; set; } = 3000;

        public int LingerMs { get; set; } = 5;
    }
}
```

`Kafka/KafkaEventPublisher.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Crm.Contracts;
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Ingestion.Infrastructure.Kafka;

/// <summary>
/// events.raw producer: acks=all + idempotence, and every call awaits its delivery report, so a 202 means the record
/// is on the log. A record that is not acknowledged within MessageTimeoutMs fails the call; librdkafka drops it, so
/// the API never holds events in memory for later (spec §4.3).
/// </summary>
internal sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private static readonly ActivitySource Source = new("Crm.Ingestion");
    private readonly Lazy<IProducer<string, byte[]>> _producer;
    private readonly ILogger<KafkaEventPublisher> _logger;

    public KafkaEventPublisher(IOptions<KafkaProducerOptions> options, ILogger<KafkaEventPublisher> logger)
    {
        _logger = logger;
        var settings = options.Value;
        // Lazy: the host is also built for OpenAPI export and for the Worker, where nothing is ever published.
        _producer = new Lazy<IProducer<string, byte[]>>(() => new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            ClientId = "crm-api",
            Acks = Acks.All,
            EnableIdempotence = true,
            LingerMs = settings.Producer.LingerMs,
            MessageTimeoutMs = settings.Producer.MessageTimeoutMs,
        }).Build());
    }

    public async Task PublishAsync(RawMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var activity = Source.StartActivity($"{KafkaTopics.EventsRaw} publish", ActivityKind.Producer);

        var headers = new Headers();
        foreach (var (name, value) in message.Headers)
        {
            headers.Add(name, Encoding.UTF8.GetBytes(value));
        }

        if (Activity.Current?.Id is { } traceParent)
        {
            headers.Add(KafkaHeaders.TraceParent, Encoding.UTF8.GetBytes(traceParent));
        }

        try
        {
            await _producer.Value.ProduceAsync(
                KafkaTopics.EventsRaw,
                new Message<string, byte[]> { Key = message.Key, Value = message.Value, Headers = headers },
                cancellationToken);
        }
        catch (KafkaException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Error.Reason);
            _logger.LogWarning(ex, "Publishing {MessageType} {EventId} to {Topic} failed: {Reason}", message.MessageType, message.EventId, KafkaTopics.EventsRaw, ex.Error.Reason);
            throw new EventPublishException($"Kafka did not acknowledge {message.MessageType} {message.EventId}: {ex.Error.Reason}", ex);
        }
    }

    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            _producer.Value.Flush(TimeSpan.FromSeconds(5));
            _producer.Value.Dispose();
        }
    }
}
```

(`ProduceException<TKey,TValue>` derives from `KafkaException`, so one catch covers delivery failures and client errors.)

- [ ] **Step 3: Module registration**

`IngestionModule.cs`:

```csharp
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Envelopes;
using Crm.Ingestion.Application.Requests;
using Crm.Ingestion.Application.Validation;
using Crm.Ingestion.Infrastructure.Kafka;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Ingestion.Infrastructure;

public static class IngestionModule
{
    /// <summary>Ingest path: validators, envelope factory, service, meter and the events.raw producer. The host supplies IClock and IIdGenerator.</summary>
    public static IServiceCollection AddIngestionModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<KafkaProducerOptions>(configuration.GetSection(KafkaProducerOptions.SectionName));
        services.AddSingleton<IValidator<TrackRequest>, TrackRequestValidator>();
        services.AddSingleton<IValidator<IdentifyRequest>, IdentifyRequestValidator>();
        services.AddSingleton<EventEnvelopeFactory>();
        services.AddSingleton<IngestMetrics>();
        services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        services.AddSingleton<IngestService>();
        return services;
    }
}
```

The Worker also calls `AddIngestionModule`; check `src/Hosts/Crm.Worker/Program.cs` registers `IClock` and `IIdGenerator` (it does today) so its container still validates. `FluentValidation` flows to Infrastructure through the project reference; if the compiler asks for it, add `<PackageReference Include="FluentValidation" />` to the Infrastructure csproj.

- [ ] **Step 4: Endpoints**

`Endpoints/IngestEndpoints.cs`:

```csharp
using Crm.Ingestion.Application;
using Crm.Ingestion.Application.Requests;
using Crm.Kernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Crm.Ingestion.Infrastructure.Endpoints;

/// <summary>202 only after Kafka acknowledged the record; nothing on this path touches the database. Authorization, rate limit and tag come from the host's group.</summary>
internal static class IngestEndpoints
{
    public static void Map(RouteGroupBuilder ingest)
    {
        Describe(ingest.MapPost("/track", TrackAsync).WithName("Track").Produces<IngestAccepted>(StatusCodes.Status202Accepted));
        Describe(ingest.MapPost("/identify", IdentifyAsync).WithName("Identify").Produces<IngestAccepted>(StatusCodes.Status202Accepted));
        Describe(ingest.MapPost("/batch", BatchAsync).WithName("Batch").Produces<BatchAccepted>(StatusCodes.Status202Accepted));
    }

    private static void Describe(RouteHandlerBuilder endpoint) => endpoint
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<object> TrackAsync(TrackRequest? request, IngestService service, ITenantContext tenant, CancellationToken cancellationToken)
        => (await service.TrackAsync(tenant.TenantId, request, cancellationToken))
            .Match<object>(accepted => TypedResults.Accepted((string?)null, accepted), error => error);

    private static async Task<object> IdentifyAsync(IdentifyRequest? request, IngestService service, ITenantContext tenant, CancellationToken cancellationToken)
        => (await service.IdentifyAsync(tenant.TenantId, request, cancellationToken))
            .Match<object>(accepted => TypedResults.Accepted((string?)null, accepted), error => error);

    private static async Task<object> BatchAsync(BatchRequest? request, IngestService service, ITenantContext tenant, CancellationToken cancellationToken)
        => (await service.BatchAsync(tenant.TenantId, request, cancellationToken))
            .Match<object>(accepted => TypedResults.Accepted((string?)null, accepted), error => error);
}
```

`Endpoints/IngestionEndpointExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Routing;

namespace Crm.Ingestion.Infrastructure.Endpoints;

public static class IngestionEndpointExtensions
{
    /// <summary>
    /// Maps /track, /identify and /batch onto a group the HOST has already secured (IngestOnly policy, `ingest` rate
    /// limit, Ingest tag): the policy names live in Tenancy, and a module may not reference another module.
    /// </summary>
    public static RouteGroupBuilder MapIngestionEndpoints(this RouteGroupBuilder ingest)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        IngestEndpoints.Map(ingest);
        return ingest;
    }
}
```

If minimal APIs refuse to infer the body for a nullable record parameter, mark it `[FromBody] TrackRequest? request` (same for the other two).

- [ ] **Step 5: Host**

`Program.cs`: add `using Crm.Ingestion.Infrastructure.Endpoints;` and `using Crm.Tenancy.Infrastructure.Auth;`, and directly after `v1.MapTenancyEndpoints();`:

```csharp
        // Composition root: Ingestion may not reference Tenancy, so the policy, limiter and tag are applied here.
        v1.MapGroup(string.Empty)
            .RequireAuthorization(AuthPolicies.IngestOnly)
            .RequireRateLimiting(RateLimitPolicies.Ingest)
            .WithTags(EndpointTags.Ingest)
            .MapIngestionEndpoints();
```

- [ ] **Step 6: Regenerate the contract and verify**

Run: `make openapi` (needs no containers) → `docs/api/openapi.json` gains `/v1/track`, `/v1/identify`, `/v1/batch`, each with `security: [{ "apiKey": [] }]` and responses 202/400/401/403/429/503. Inspect the diff: the `BatchItemResult`, `BatchItem`, `TrackRequest` schemas must not be empty objects.

Run: `dotnet test Crm.sln --filter "Category!=Integration"` → all green (CT-001 now passes). `dotnet build Crm.sln -warnaserror` → 0 warnings. `dotnet test tests/Crm.ArchTests` → AT-001, AT-003, AT-004, AT-005 green.

- [ ] **Step 7: Commit**

```bash
git add src/Modules/Ingestion/Ingestion.Infrastructure src/Hosts/Crm.Api/Program.cs tests/Crm.Api.UnitTests/OpenApiDocumentTests.cs docs/api/openapi.json
git commit -m "feat(ingestion): /v1/track, /identify and /batch publish to events.raw with an idempotent acks=all producer (CT-001)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Integration tests on real Kafka; retire the ingest probe (IT-001/002 ingest half, IT-003, IT-039, IT-033, IT-035)

**Files:**
- Modify: `tests/Crm.IntegrationTests/Crm.IntegrationTests.csproj` (add `<PackageReference Include="Confluent.Kafka" />`), `Fixtures/CrmStackFixture.cs`
- Create: `tests/Crm.IntegrationTests/Fixtures/RawTopicReader.cs`, `Ingestion/IngestClient.cs`, `Ingestion/TrackTests.cs`, `Ingestion/IdentifyAndBatchTests.cs`, `Ingestion/KafkaUnavailableTests.cs`
- Modify: `tests/Crm.IntegrationTests/Tenancy/ApiKeyTests.cs`, `Tenancy/RateLimitTests.cs` (probe → `/v1/track`), `src/Modules/Tenancy/Tenancy.Infrastructure/Endpoints/TestProbeEndpoints.cs` (delete the `ingest-probe` mapping and any using/tag that becomes unused)

**Interfaces:**
- Consumes: `CrmStackFixture`, `CrmApiFactory.CreateApiClient()`, `TenancySeed.CreateTenantAsync/AddApiKeyAsync/AccessToken`, `SeededTenant.TenantId`, `SeededApiKey.Plaintext`, `AuthSchemes.ApiKeyHeader`; `KafkaTopics`, `KafkaHeaders`, `ContractJson`, `TrackEventRaw`, `IdentifyRaw`.
- Produces: `CrmStackFixture.PauseKafkaAsync()/UnpauseKafkaAsync()`; `RawTopicReader.ReadTenantAsync(string bootstrapServers, Guid tenantId, int expected, TimeSpan timeout)`.

- [ ] **Step 1: Fixture — topics and pause**

In `CrmStackFixture.cs`, call `await CreateTopicsAsync();` at the end of `InitializeAsync` and add:

```csharp
    /// <summary>compose's kafka-init creates these with 12 partitions and auto-creation off; mirror it so keys spread the same way.</summary>
    private async Task CreateTopicsAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaBootstrapServers }).Build();
        try
        {
            await admin.CreateTopicsAsync(new[] { KafkaTopics.EventsRaw, KafkaTopics.EventsProcessed, KafkaTopics.EventsDlq }
                .Select(name => new TopicSpecification { Name = name, NumPartitions = KafkaTopics.Partitions, ReplicationFactor = 1 }));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // created by an earlier fixture instance or by broker auto-creation
        }
    }

    public Task PauseKafkaAsync() => ((IContainer)_kafka).PauseAsync();

    public Task UnpauseKafkaAsync() => ((IContainer)_kafka).UnpauseAsync();
```

(usings: `Confluent.Kafka`, `Confluent.Kafka.Admin`, `Crm.Contracts`.)

- [ ] **Step 2: Topic reader**

`Fixtures/RawTopicReader.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Crm.Contracts;

namespace Crm.IntegrationTests.Fixtures;

public sealed record RawRecord(string Key, string MessageType, JsonElement Payload, IReadOnlyDictionary<string, string> Headers);

public static class RawTopicReader
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads events.raw from the beginning with a throw-away group and returns the records of one tenant (tests create
    /// a tenant each, so this isolates them). Waits until <paramref name="expected"/> records arrived and then a quiet
    /// period longer, so "exactly N" assertions can see an unexpected extra record.
    /// </summary>
    public static Task<IReadOnlyList<RawRecord>> ReadTenantAsync(string bootstrapServers, Guid tenantId, int expected, TimeSpan timeout)
        => Task.Run<IReadOnlyList<RawRecord>>(() =>
        {
            using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = $"it-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            }).Build();
            consumer.Subscribe(KafkaTopics.EventsRaw);

            var tenant = tenantId.ToString("N");
            var records = new List<RawRecord>();
            var deadline = DateTime.UtcNow + timeout;
            DateTime? quietUntil = null;
            while (DateTime.UtcNow < deadline && (quietUntil is null || DateTime.UtcNow < quietUntil))
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (result?.Message is null)
                {
                    continue;
                }

                var headers = result.Message.Headers.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes()), StringComparer.Ordinal);
                if (!headers.TryGetValue(KafkaHeaders.TenantId, out var owner) || owner != tenant)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(result.Message.Value);
                records.Add(new RawRecord(result.Message.Key, headers[KafkaHeaders.MessageType], document.RootElement.Clone(), headers));
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

- [ ] **Step 3: Request helper**

`Ingestion/IngestClient.cs`:

```csharp
using System.Net.Http.Json;
using Crm.Tenancy.Infrastructure.Auth;

namespace Crm.IntegrationTests.Ingestion;

internal static class IngestClient
{
    public static Task<HttpResponseMessage> PostWithKeyAsync(this HttpClient client, string path, string apiKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(AuthSchemes.ApiKeyHeader, apiKey);
        return client.SendAsync(request);
    }
}
```

- [ ] **Step 4: Track tests**

`Ingestion/TrackTests.cs` — class `TrackTests(CrmStackFixture stack) : IAsyncLifetime`, attributes `[Collection(StackCollectionDefinition.Name)]`, `[Trait("Category", "Integration")]`, factory/client set up exactly like `Ops/HealthTests.cs` but with `_factory.CreateApiClient()`. Tests:

```csharp
    [Fact]
    public async Task IT001_track_returns_202_with_a_v7_event_id_and_the_record_is_on_events_raw()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase", properties = new { amount = 49.9 } });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var eventId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eventId").GetGuid();
        eventId.Version.Should().Be(7);

        var records = await RawTopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(30));
        var record = records.Should().ContainSingle().Subject;
        record.Key.Should().Be($"{tenant.TenantId:N}:u1");
        record.MessageType.Should().Be(nameof(TrackEventRaw));
        record.Headers[KafkaHeaders.ContentType].Should().Be(KafkaHeaders.JsonContentType);
        record.Headers.Should().ContainKey(KafkaHeaders.TraceParent);
        var payload = record.Payload.Deserialize<TrackEventRaw>(ContractJson.Options)!;
        payload.EventId.Should().Be(eventId);
        payload.TenantId.Should().Be(tenant.TenantId);
        payload.Event.Should().Be("purchase");
        payload.Properties.GetProperty("amount").GetDouble().Should().Be(49.9);
        payload.ReceivedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task IT001_p95_latency_of_twenty_calls_is_within_budget()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        (await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "warm", @event = "warmup" })).Dispose();

        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase" });
            stopwatch.Stop();
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        // NFR-02's 50 ms holds with a local broker. With a remote engine (TESTCONTAINERS_HOST_OVERRIDE set) every
        // acks=all round trip crosses the network, so only a loose ceiling is meaningful there.
        var remote = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTCONTAINERS_HOST_OVERRIDE"));
        var p95 = timings.Order().ElementAt(18);
        p95.Should().BeLessThan(remote ? 500 : 50, $"timings: {string.Join(", ", timings.Select(t => t.ToString("F0", CultureInfo.InvariantCulture)))}");
    }

    [Fact]
    public async Task Validation_failures_are_400_problems_with_field_paths_and_publish_nothing()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "bad name!" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("ingest.invalid");
        problem.GetProperty("detail").GetString().Should().Be("event must match [A-Za-z0-9_]{1,64}");
        problem.GetProperty("errors").GetProperty("event")[0].GetString().Should().Be("must match [A-Za-z0-9_]{1,64}");
        problem.GetProperty("instance").GetString().Should().Be("/v1/track");

        (await RawTopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, tenant.TenantId, expected: 1, TimeSpan.FromSeconds(5))).Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_json_is_a_400_problem()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/track") { Content = new StringContent("{\"userId\":", Encoding.UTF8, "application/json") };
        request.Headers.Add(AuthSchemes.ApiKeyHeader, key.Plaintext);

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Ingest_needs_an_api_key_and_refuses_a_dashboard_token()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var body = new { userId = "u1", @event = "purchase" };

        using var anonymous = await _client.PostAsJsonAsync("/v1/track", body);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var withJwt = new HttpRequestMessage(HttpMethod.Post, "/v1/track") { Content = JsonContent.Create(body) };
        withJwt.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, "Admin"));
        using var forbidden = await _client.SendAsync(withJwt);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Metrics_expose_the_series_the_dashboard_queries()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        (await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "purchase" })).Dispose();
        (await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "u1", @event = "bad name!" })).Dispose();

        var body = string.Empty;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            body = await _client.GetStringAsync(new Uri("/metrics", UriKind.Relative));
            if (body.Contains("crm_ingest_events_total", StringComparison.Ordinal) && body.Contains("crm_ingest_rejected_total", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(500);
        }

        body.Should().Contain("crm_ingest_events_total{").And.Contain("type=\"track\"");
        body.Should().Contain("crm_ingest_rejected_total{").And.Contain("reason=\"validation\"");
    }
```

If the exported names differ (for example `crm_ingest_events_total_total`), fix the instrument names in `IngestMetrics` — the exported names are the contract (ruling D10) — and say so in your report.

- [ ] **Step 5: Identify and batch tests**

`Ingestion/IdentifyAndBatchTests.cs`, same class scaffolding:

```csharp
    [Fact]
    public async Task IT002_identify_is_accepted_and_lands_on_events_raw_as_IdentifyRaw()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        using var response = await _client.PostWithKeyAsync("/v1/identify", key.Plaintext, new { userId = "u2", anonymousId = "a9", traits = new { plan = "pro" } });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var record = (await RawTopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, tenant.TenantId, 1, TimeSpan.FromSeconds(30))).Should().ContainSingle().Subject;
        record.MessageType.Should().Be(nameof(IdentifyRaw));
        record.Key.Should().Be($"{tenant.TenantId:N}:u2");
        var payload = record.Payload.Deserialize<IdentifyRaw>(ContractJson.Options)!;
        payload.AnonymousId.Should().Be("a9");
        payload.Traits.GetProperty("plan").GetString().Should().Be("pro");
    }

    [Fact]
    public async Task IT003_batch_returns_per_item_results_and_publishes_only_the_accepted_items()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        var batch = new
        {
            events = new object[]
            {
                new { type = "track", userId = "u1", @event = "app_open" },
                new { type = "track", userId = "u1", @event = "bad name!" },
                new { type = "identify", userId = "u1", traits = new { plan = "pro" } },
            },
        };

        using var response = await _client.PostWithKeyAsync("/v1/batch", key.Plaintext, batch);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accepted").GetInt32().Should().Be(2);
        body.GetProperty("rejected").GetInt32().Should().Be(1);
        var results = body.GetProperty("results");
        results[0].GetProperty("eventId").GetGuid().Should().NotBeEmpty();
        results[0].TryGetProperty("errors", out _).Should().BeFalse();
        results[1].TryGetProperty("eventId", out _).Should().BeFalse();
        results[1].GetProperty("errors").GetProperty("events[1].event")[0].GetString().Should().Be("must match [A-Za-z0-9_]{1,64}");

        var records = await RawTopicReader.ReadTenantAsync(stack.KafkaBootstrapServers, tenant.TenantId, 2, TimeSpan.FromSeconds(30));
        records.Select(r => r.MessageType).Should().BeEquivalentTo([nameof(TrackEventRaw), nameof(IdentifyRaw)], "exactly the two accepted items");
    }

    [Fact]
    public async Task A_batch_of_501_items_is_a_400()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        var batch = new { events = Enumerable.Range(0, 501).Select(_ => new { type = "track", userId = "u1", @event = "x" }).ToArray() };

        using var response = await _client.PostWithKeyAsync("/v1/batch", key.Plaintext, batch);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("events")[0].GetString().Should().Be("at most 500 items");
    }
```

- [ ] **Step 6: Kafka unavailable (IT-039)**

`Ingestion/KafkaUnavailableTests.cs`, same scaffolding:

```csharp
    [Fact]
    public async Task IT039_kafka_unavailable_is_503_with_retry_after_and_recovers()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        (await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "warm", @event = "warmup" })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        await stack.PauseKafkaAsync();
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "during-outage", @event = "purchase" });
            stopwatch.Stop();

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("ingest.unavailable");
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the request fails at message.timeout.ms instead of hanging");
        }
        finally
        {
            await stack.UnpauseKafkaAsync();
        }

        // Recovery: the same producer instance reconnects by itself; poll until the broker is serving again so the
        // next test class in the collection starts from a healthy stack.
        var recovered = HttpStatusCode.ServiceUnavailable;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60) && recovered != HttpStatusCode.Accepted)
        {
            using var retry = await _client.PostWithKeyAsync("/v1/track", key.Plaintext, new { userId = "after-outage", @event = "purchase" });
            recovered = retry.StatusCode;
        }

        recovered.Should().Be(HttpStatusCode.Accepted);
    }
```

The test deliberately does not assert that the `during-outage` record is absent from the topic (ruling D8).

- [ ] **Step 7: Repoint IT-033 / IT-035 and delete the probe**

- `ApiKeyTests.cs`: `IngestProbe` becomes `private const string Ingest = "/v1/track";` and every request to it gets `Content = JsonContent.Create(new { userId = "u-it", @event = "probe" })` (rename usages). Expected statuses do not change (202 / 401).
- `RateLimitTests.cs`: the three `"/v1/_test/ingest-probe"` requests become `/v1/track` with the same JSON content. Update the class or test comments that mention the probe.
- `TestProbeEndpoints.cs`: delete the `ingest-probe` mapping; update the summary to name only `read-probe`/`write-probe` and the Segmentation plan. Remove usings that become unused.

- [ ] **Step 8: Run and commit**

Run (environment prefix from Global Constraints, timeout ≥ 25 min): `dotnet test Crm.sln --filter "Category=Integration"` → all green, 0 skipped (24 existing + 10 new = 34). Then `dotnet build Crm.sln -warnaserror` → 0 warnings and `dotnet test Crm.sln --filter "Category!=Integration"` → green.

If `IT035` now takes longer than before because each accepted request is a Kafka round trip, that is expected; its slow-engine branch already covers it. Report the elapsed time it logged if it fails.

```bash
git add tests/Crm.IntegrationTests src/Modules/Tenancy/Tenancy.Infrastructure/Endpoints/TestProbeEndpoints.cs
git commit -m "test(integration): ingest endpoints against real Kafka (IT-001/002 ingest half, IT-003, IT-039); IT-033 and IT-035 use /v1/track and the ingest probe is gone

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Cold-start probe, README quickstart, documentation

**Files:**
- Modify: `tests/Crm.LoadTests/coldstart.sh`, `README.md`, `docs/api/README.md`, `docs/test/unit.md`, `docs/test/integration.md`, `docs/test/e2e-load.md`, `docs/superpowers/specs/2026-09-15-crm-slice1-design.md` (§4.1, §4.3), `docs/superpowers/specs/2026-09-16-foundation-design.md` (ruling P4 note), `docs/dev-setup.md`

- [ ] **Step 1: `coldstart.sh` probe → `/v1/track`**

Replace the login/`api-keys` probe block with (nothing here may trip `set -e`; the result row must always print):

```bash
# Quickstart probe (ruling D13): the README's first event. `make seed` wrote the key to .env.local.
api_key=$(sed -n 's/^CRM_API_KEY=//p' .env.local 2>/dev/null | head -n 1 || true)
if [ -n "$api_key" ]; then
  probe_status=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE_URL/v1/track" \
    -H "X-Api-Key: $api_key" -H 'Content-Type: application/json' \
    -d '{"userId":"u-123","event":"purchase","properties":{"amount":49.9}}' || true)
else
  probe_status="no-api-key"
fi
```

and the verdict line becomes `[ "$probe_status" = "202" ] || verdict=fail`. Update the header comment: the probe is `POST /v1/track → 202`; only the 5k-profile / 100k-event seed volume is still outstanding (Profiles plan). Verify: `bash -n tests/Crm.LoadTests/coldstart.sh`; do not execute it. (This script is the one place that reads `.env.local`; you still must not.)

- [ ] **Step 2: README**

- Status table: `Implementation: Ingestion, Profiles pipeline | Planned` becomes two rows — `Implementation: Ingestion API (track, identify, batch → Kafka) | Complete` and `Implementation: Profiles pipeline (Worker consumer, identity resolution) | Planned`. Adjust the status paragraph's "land next" sentence accordingly.
- Quickstart: the third command becomes the first event, replacing the login curl and removing the "Once the Ingestion plan lands" block:

````markdown
```bash
make up          # infrastructure + api + worker + web, builds images, waits until healthy
make seed        # demo tenant, Admin + Viewer users, an API key written to .env.local
source .env.local && curl -X POST http://localhost:8080/v1/track \
  -H "X-Api-Key: $CRM_API_KEY" -H "Content-Type: application/json" \
  -d '{"userId":"u-123","event":"purchase","properties":{"amount":49.9}}'    # 202 {"eventId":"…"}
```
````

  Add one sentence below: the event is now on Kafka topic `events.raw`; the Worker that turns it into a profile and a timeline entry arrives with the Profiles plan. Leave the "Measured results" row untouched — the controller replaces it after re-measuring.

- [ ] **Step 3: API reference (`docs/api/README.md`)**

In "Ingest": state the limits (`userId` ≤ 128 characters, `anonymousId` ≤ 256, `properties`/`traits` JSON objects ≤ 32 KB, blank identifiers count as absent); that the event id is a UUIDv7 ordered by receive time; that `/batch` answers `202` even when every item is rejected, `400` only for the envelope (`events` missing, empty or > 500), and `503` for the whole request when Kafka does not acknowledge any accepted item; and the `503` semantics: *unconfirmed, not absent — retry; the pipeline tolerates the duplicate as a separate event, so send the retry only when the business event warrants it*. Error codes: `ingest.invalid`, `ingest.unavailable`. Delete the sentence in "Tenancy" saying the `apiKey` scheme is referenced by no operation yet.

- [ ] **Step 4: Test specification**

- `docs/test/unit.md`: for UT-001 … UT-008 replace each `- Planned: …` bullet with `- Implemented: <actual file>` (`TrackValidatorTests.cs`, `IdentifyValidatorTests.cs`, `BatchValidatorTests.cs`, `EventEnvelopeTests.cs` under `tests/Ingestion.UnitTests/`).
- `docs/test/integration.md`: IT-003 → `- Implemented: tests/Crm.IntegrationTests/Ingestion/IdentifyAndBatchTests.cs`; IT-039 → `- Implemented: tests/Crm.IntegrationTests/Ingestion/KafkaUnavailableTests.cs`, plus a note: the test does not assert the outage-time record is absent from the topic — a request the broker received just before stalling may still be appended (ruling D8). IT-001 and IT-002 keep `Planned:` and gain `- Plan D note: the ingest half (202, record on events.raw) is implemented in tests/Crm.IntegrationTests/Ingestion/{TrackTests,IdentifyAndBatchTests}.cs; the profile and timeline half arrives with the Profiles plan.` The three "Plan B note" bullets that mention `ingest-probe`: IT-033 and IT-035 now run against `/v1/track`; only `read-probe` and `write-probe` remain.
- `docs/test/e2e-load.md` LT-005: the probe is now the README curl returning 202; only the seed volume is outstanding.

- [ ] **Step 5: Specs**

- Slice-1 design §4.1: append a short "Rulings (plan D)" paragraph covering D5 (key and headers), D6 (extra limits), D7, D8 (producer settings and 503 semantics), D9 (batch semantics), D10 (metric labels).
- Foundation design, ruling P4 paragraph: `ingest-probe` was removed by plan D; `read-probe` and `write-probe` remain until Segmentation. §12 decision row "Test-only ingest probe endpoint for IT-035" → mark as removed by plan D.
- `docs/dev-setup.md` "Logging in" section: add the track curl (with `source .env.local`) after the login example.

- [ ] **Step 6: Verify and commit**

Run: `make docs-check` → `0 errors`; `bash -n tests/Crm.LoadTests/coldstart.sh`; `python3 -m unittest discover -s tools/tests` → OK.

```bash
git add tests/Crm.LoadTests/coldstart.sh README.md docs
git commit -m "docs: ingest API reference, quickstart sends the first event, LT-005 probe is /v1/track

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Live verification and LT-005 re-measurement (controller-run)

- [ ] **Step 1:** `make tunnel-observability PROMETHEUS_HOST_PORT=19090`, then `caffeinate -i make up-observability MSSQL_HOST_PORT=11433 PROMETHEUS_HOST_PORT=19090 MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest`, `make seed`.
- [ ] **Step 2:** Send the README's track request (via a script file or `python3 urllib`; the hook blocks `curl` in the agent shell) → `202` with an `eventId`; an invalid event → `400` with `errors.event`; a request without a key → `401`.
- [ ] **Step 3:** The record is on the topic: `docker exec crm-kafka-1 /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic events.raw --from-beginning --max-messages 1 --timeout-ms 10000 --property print.key=true --property print.headers=true`.
- [ ] **Step 4:** Prometheus has the series: query `crm_ingest_events_total` and `crm_ingest_rejected_total` on `http://localhost:9090/api/v1/query` → non-empty; the Grafana "Ingest rate" panel draws a line.
- [ ] **Step 5:** Jaeger shows a `Crm.Api` trace for `POST /v1/track` containing the `events.raw publish` producer span.
- [ ] **Step 6:** `make down …`, then `caffeinate -i bash tests/Crm.LoadTests/coldstart.sh MSSQL_HOST_PORT=11433 MSSQL_IMAGE=…` → exit 0, probe `202`. Dispatch a one-line README change replacing the "Measured results" row with the new row verbatim; commit `docs: LT-005 re-measured with the /v1/track probe`.

---

## Exit gate

1. `dotnet build Crm.sln -warnaserror` → 0 warnings; unit/arch/contract suites green, including UT-001 … UT-008 and the updated CT-001.
2. Integration suite green with 0 skipped on the compose images (34 tests).
3. `make openapi` produces no diff against the committed document; `make docs-check` 0 errors; `tools/tests` green; `make compose-check` two OK lines.
4. Task 8 checks all held; `coldstart.sh` exits 0 with probe `202`.
5. Final whole-branch review (Opus), one fix wave, user pushes, CI green.

## Self-review notes

- **Spec coverage:** §4.1 endpoints, validation list, UUIDv7, tenant and receive-time stamping, acks=all, `202 {eventId}`, "no database access" → Tasks 2–5 (AT-003 guards the last); per-key rate limit and 429 → existing `ingest` policy applied by the host (Task 5), exercised by IT-035 on `/v1/track` (Task 6); §3.5 topic key → Task 3; §4.3 "Kafka unavailable → 503 + Retry-After, never buffers" → Tasks 1, 4, 5, 6; §8 `crm_ingest_*` metrics and producer span with `traceparent` → Tasks 4, 5, 6. Worker-side §4.2 and the rest of §4.3 → Profiles plan.
- **Deliberately not done:** consumer, DLQ, idempotent insert, profile reads (Profiles plan); `crm_consumer_lag`; rate-limit rejections in ingest metrics (D10); SDKs; request signing; per-tenant quotas.
- **Type/name consistency:** `RawMessage(EventId, MessageType, Key, Value, Headers)` is identical in Tasks 3, 4, 5 and the test fake; `IEventPublisher.PublishAsync(RawMessage, CancellationToken)` likewise; error codes `ingest.invalid` / `ingest.unavailable` match across service, tests and docs; header `tenant-id` uses `N` format in the factory, the reader and ruling D5; metric names in `IngestMetrics`, the integration test and the Grafana dashboard agree on the exported form.

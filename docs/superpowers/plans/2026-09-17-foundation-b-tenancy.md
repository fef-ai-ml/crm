# Foundation Plan B — Tenancy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the Tenancy module: `identity` schema and migration, JWT + rotating refresh cookie, API keys with Redis-cached verification, tenant resolution, role policies, ingest rate limiter, problem+json mapping, tenant bootstrap, the committed OpenAPI document, and `make migrate` / `make seed`.

**Architecture:** Tenancy follows the module layout from plan A (`Tenancy.Domain` → `Tenancy.Application` → `Tenancy.Infrastructure`); the host references Infrastructure only. Authentication is two ASP.NET Core schemes (JWT bearer, API key) behind one policy scheme; a middleware after authentication copies the principal's claims into the scoped `TenantContext`, which every tenant-filtered `DbContext` reads per query. Endpoint handlers return either an `IResult` or a Kernel `Error`; one endpoint filter in `Crm.Api` turns `Error` into problem+json through `ResultProblemMapper`.

**Tech Stack:** .NET 10 (SDK 10.0.400), ASP.NET Core minimal APIs, ASP.NET Core Identity (`AddIdentityCore`) on EF Core 10.0.12 / SQL Server, `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.12 (`JsonWebTokenHandler`), `System.Threading.RateLimiting`, StackExchange.Redis 3.2.1, xUnit 2.9.3, FluentAssertions 7.2.2, NSubstitute 6.2.0, Testcontainers 4.15.0.

**Spec:** `docs/superpowers/specs/2026-09-16-foundation-design.md` (§6, §7.1, §8.3, §10–§12; binding). Parent: `docs/superpowers/specs/2026-09-15-crm-slice1-design.md`. Data dictionary: `docs/data-dictionary/identity.md`. Test cases: `docs/test/unit.md`, `docs/test/integration.md`, `docs/test/architecture-contract.md`.

**Test cases that turn green in this plan:** UT-050, UT-051, UT-052, UT-053, UT-056, AT-006 (first real entities), CT-001, IT-032, IT-033, IT-034, IT-035, IT-036, IT-037, IT-038. Already green from plan A: UT-054, UT-055, UT-057, AT-001…AT-005, CT-003, CT-004, CT-005, IT-041. Every other case in `docs/test/` stays pending.

## Global Constraints

- .NET SDK `10.0.400` (`global.json`); `TreatWarningsAsErrors=true`; `AnalysisLevel=latest-recommended`; IDE0005 is an error. No `#pragma warning disable`, no `[SuppressMessage]`, no `NoWarn` additions. When an analyzer fires, fix the code and note it in the report.
- Central package management: `PackageReference` carries no `Version`; versions live in `Directory.Packages.props`.
- xunit 2.9.3 ships no global using: every test file has `using Xunit;`. Usings are alphabetical with `System.*` first; no unused usings.
- Host entry points stay namespaced classes (`Crm.Api.Program` with `static Task Main`); never top-level statements, never `public partial class Program;`.
- Modules reference only `Crm.Kernel` and `Crm.Contracts` (AT-001) and never `Crm.Hosting` or a host (AT-004). Hosts reference module `*.Infrastructure` projects only (AT-005).
- Namespaces: `Crm.Tenancy.Domain`, `Crm.Tenancy.Application`, `Crm.Tenancy.Infrastructure` (project/assembly names stay `Tenancy.Domain`, …).
- Schema `identity`; migrations history table `__EFMigrationsHistory_identity` in that schema.
- API key format `crm_live_` + 32 base62 characters; `Prefix` = first 8 of those 32; stored as SHA-256 (`binary(32)`); compared with `CryptographicOperations.FixedTimeEquals`.
- Access token 15 min, claims `sub`, `tenant_id`, `role`, `jti`; refresh token 32 random bytes base64url, 7 d, cookie `crm_refresh` with `HttpOnly; Secure; SameSite=Strict; Path=/v1/auth`.
- Rate-limit policy name `ingest`: token bucket, 2,000 tokens, replenish 2,000 per minute, partitioned by API-key prefix, 429 with `Retry-After`.
- Problem `type` is `https://crm.example/problems/<kind-lowercase>`; every problem body carries `traceId`.
- Integration tests carry `[Trait("Category", "Integration")]`, join `[Collection(StackCollectionDefinition.Name)]`, and use `CrmApiFactory`.
- Container engine on the authoring machine is remote. Before any docker/make/Testcontainers command:
  `export DOCKER_HOST=unix://$HOME/.local/share/containers/home-lab.sock MSSQL_HOST_PORT=11433 MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest TESTCONTAINERS_HOST_OVERRIDE=home-lab TESTCONTAINERS_RYUK_DISABLED=true`.
  Never run `podman machine`, `docker context` (other than `ls`/`inspect`), `make down`, `make tunnel-down`, `pkill`; never edit `~/.config`, `~/.local`, `deploy/.env`. `curl` is blocked locally; use `python3` + `urllib`.
- Commit trailer, verbatim: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Do not push.

## Rulings made while planning (the spec is silent or contradicts shipped code)

| # | Ruling | Why |
|---|---|---|
| P1 | API-key lookup and verification live in an authentication handler (`ApiKey` scheme); `TenantResolutionMiddleware` only copies claims into `TenantContext`. | Authorization policies need an authenticated principal; the observable behaviour of spec §6.2 is unchanged. |
| P2 | Redis value for `apikey:{prefix}` is `"{tenantId:N}:{keyId:N}:{keyHashHex}"`, not the bare tenant id. | A cache hit must still verify the full key, otherwise a key with a changed suffix authenticates (IT-033). The data dictionary row is updated in Task 10. |
| P3 | `AppUser` lives in `Tenancy.Infrastructure` (it derives from `IdentityUser`); `TokenService` lives in Infrastructure too and `Tenancy.UnitTests` references Infrastructure. | Domain/Application stay free of ASP.NET Identity and all `Microsoft.IdentityModel.*` assemblies come from one transitive source (mixed versions break at run time). |
| P4 | Cases that name endpoints from later plans use test-only probes mapped only in the `Testing` environment and excluded from OpenAPI: `POST /v1/_test/ingest-probe` (Ingest, 202) stands in for `POST /v1/track`; `GET /v1/_test/read-probe` (AdminOrViewer, reads tenant data) for `GET /v1/segments`; `POST /v1/_test/write-probe` (AdminOnly) for `POST /v1/segments` and `/run`. | Spec §6.4 already sanctions the ingest probe; the same device keeps IT-032/033/037 honest now. The Ingestion and Segmentation plans repoint the tests and delete the probes. |
| P5 | The EF migration class is `InitialIdentity` (file `<timestamp>_InitialIdentity.cs`). | C# identifiers cannot start with a digit; spec's `0001_` is a label. |
| P6 | `POST /v1/tenants` accepts optional `viewerEmail` + `viewerPassword` and creates a Viewer user when both are present. | Spec §8.3 requires `make seed` to create a Viewer and defines no user-management endpoint; the endpoint is Development-only. |
| P7 | OpenAPI export maps the Development-only bootstrap endpoint (condition: `IsDevelopment() || exportPath is not null`) and runs after endpoint mapping. | The committed document must not depend on the environment CI happens to export in; plan A exported before any endpoint was mapped. |
| P8 | CT-001 in this plan asserts no drift plus an exact path set for the Tenancy endpoints; "exactly the endpoints in api/README.md" completes when the remaining modules land. | README already documents endpoints of later plans. |
| P9 | The Development start-up block no longer resolves a magic tenant before `MigrateAsync()`. | Migrations never evaluate query filters; removes plan A review minor #10. |

## File Structure

```
src/Modules/Tenancy/Tenancy.Domain/
  Tenant.cs                 aggregate without TenantId; slug rules
  ApiKey.cs                 Entity; Generate/Verify/Revoke/Touch; ApiKeyVerification enum; GeneratedApiKey record
  RefreshToken.cs           Entity; Issue/HashOf/IsActive/Revoke; IssuedRefreshToken record
  Roles.cs                  "Admin", "Viewer" constants
src/Modules/Tenancy/Tenancy.Application/
  AuthOptions.cs            Auth:* configuration (issuer, audience, signing key, lifetimes)
  ITokenService.cs          AccessToken record + interface
  TenancyClaims.cs          claim names and kind values
  TenancyErrors.cs          Error catalogue (codes/messages) used by endpoints
src/Modules/Tenancy/Tenancy.Infrastructure/
  AppUser.cs                IdentityUser + TenantId + DisplayName
  TenancyDbContext.cs       IdentityDbContext, schema identity, filters, configurations
  Configurations/*.cs       TenantConfiguration, ApiKeyConfiguration, RefreshTokenConfiguration
  TenancyDbContextFactory.cs design-time factory for dotnet-ef
  Migrations/*              InitialIdentity
  Tokens/TokenService.cs    JWT issuing (JsonWebTokenHandler)
  Auth/AuthSchemes.cs       scheme/policy/claim name constants
  Auth/ApiKeyAuthenticator.cs      Redis-cached verification + LastUsedAt throttle
  Auth/ApiKeyAuthenticationHandler.cs
  Auth/TenantResolutionMiddleware.cs
  Auth/RefreshCookie.cs     cookie write/clear rules
  Auth/AuthService.cs       login / refresh rotation / logout
  Auth/ApiKeyUsageTracker.cs  once-per-minute LastUsedAt throttle
  Tenants/TenantBootstrapService.cs  tenant + Admin (+ Viewer) in one transaction
  Endpoints/AuthEndpoints.cs, TenantEndpoints.cs, ApiKeyEndpoints.cs, TestProbeEndpoints.cs
  Endpoints/TenancyEndpointExtensions.cs   MapTenancyEndpoints / MapTenancyBootstrap / MapTenancyTestProbes
  TenancyModule.cs          AddTenancyModule (DbContext, Identity, auth, policies, rate limiter)
src/Hosts/Crm.Api/
  Problems/ResultProblemMapper.cs, ErrorResultFilter.cs, UnhandledExceptionHandler.cs, ProblemDefaults.cs
  OpenApi/CrmDocumentTransformer.cs
  Program.cs                pipeline order, endpoint mapping, export after mapping
tests/Tenancy.UnitTests/    ApiKeyTests.cs, RefreshTokenTests.cs, TokenServiceTests.cs
tests/Crm.Api.UnitTests/    ProblemDetailsMapperTests.cs
tests/Crm.ArchTests/        TenantScopingTests.cs (adds TenancyDbContext)
tests/Crm.IntegrationTests/ Fixtures/TenancySeed.cs, Fixtures/CrmApiFactory.cs, Fixtures/CrmStackFixture.cs,
                            Tenancy/{SchemaTests,IsolationTests,ApiKeyTests,RateLimitTests,AuthTests,BootstrapTests}.cs,
                            Contract/OpenApiDocumentTests.cs
tools/seed.py               make seed
docs/api/openapi.json       committed export
```

---

### Task 1: Tenancy domain model (UT-050, UT-051, UT-052)

**Files:**
- Create: `src/Modules/Tenancy/Tenancy.Domain/Tenant.cs`, `ApiKey.cs`, `RefreshToken.cs`, `Roles.cs`
- Test: `tests/Tenancy.UnitTests/ApiKeyTests.cs`, `tests/Tenancy.UnitTests/RefreshTokenTests.cs`, `tests/Tenancy.UnitTests/TenantTests.cs`
- Modify: `tests/Tenancy.UnitTests/Tenancy.UnitTests.csproj` (add reference to `Tenancy.Domain`; it is already transitive through Application, so add nothing if the build resolves `Crm.Tenancy.Domain`)

**Interfaces:**
- Consumes: `Crm.Kernel.Entity` (`Id`, `TenantId`, `CreatedAt`, `UpdatedAt`; setters are `protected internal`, so Domain code cannot set `TenantId` — the save interceptor does), `Crm.Kernel.Result<T>`, `Crm.Kernel.Error`.
- Produces:
  - `Tenant.Create(string name, string slug, DateTime utcNow) : Result<Tenant>`; properties `Id`, `Name`, `Slug`, `Status` (`TenantStatus.Active|Suspended`), `CreatedAt`.
  - `ApiKey.Generate(string name, string createdByUserId) : GeneratedApiKey` where `record GeneratedApiKey(ApiKey Entity, string Plaintext)`; `ApiKey.Verify(string plaintext) : ApiKeyVerification` (`Valid`, `Invalid`, `Revoked`); `ApiKey.Revoke(DateTime utcNow)`; `ApiKey.Touch(DateTime utcNow)`; static `ApiKey.TryParsePrefix(string? plaintext, out string prefix) : bool`; static `ApiKey.Hash(string plaintext) : byte[]`; constants `ApiKey.KeyPrefix = "crm_live_"`, `ApiKey.PrefixLength = 8`, `ApiKey.SecretLength = 32`.
  - `RefreshToken.Issue(string userId, DateTime utcNow, TimeSpan lifetime) : IssuedRefreshToken` where `record IssuedRefreshToken(RefreshToken Entity, string Plaintext)`; `RefreshToken.HashOf(string plaintext) : byte[]`; `IsActive(DateTime utcNow) : bool`; `Revoke(DateTime utcNow, Guid? replacedBy = null)`.
  - `Roles.Admin = "Admin"`, `Roles.Viewer = "Viewer"`, `Roles.All`.

- [ ] **Step 1: Write the failing tests**

`tests/Tenancy.UnitTests/ApiKeyTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public partial class ApiKeyTests
{
    [GeneratedRegex("^crm_live_[0-9A-Za-z]{32}$")]
    private static partial Regex KeyFormat();

    [Fact]
    public void UT050_generated_keys_match_the_format_and_prefixes_are_distinct()
    {
        var generated = Enumerable.Range(0, 1000).Select(_ => ApiKey.Generate("sdk", "user-1")).ToList();

        generated.Should().OnlyContain(g => KeyFormat().IsMatch(g.Plaintext));
        generated.Should().OnlyContain(g => g.Entity.Prefix == g.Plaintext.Substring(ApiKey.KeyPrefix.Length, ApiKey.PrefixLength));
        generated.Select(g => g.Entity.Prefix).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void UT051_entity_keeps_only_the_sha256_hash()
    {
        var generated = ApiKey.Generate("sdk", "user-1");

        generated.Entity.KeyHash.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(generated.Plaintext)));
        typeof(ApiKey).GetProperties().Select(p => p.Name).Should().NotContain(["Plaintext", "Key", "Secret"]);
        generated.Entity.Verify(generated.Plaintext).Should().Be(ApiKeyVerification.Valid);
    }

    [Fact]
    public void UT051_verify_rejects_a_key_with_one_character_changed()
    {
        var generated = ApiKey.Generate("sdk", "user-1");
        var last = generated.Plaintext[^1];
        var tampered = generated.Plaintext[..^1] + (last == 'a' ? 'b' : 'a');

        generated.Entity.Verify(tampered).Should().Be(ApiKeyVerification.Invalid);
        generated.Entity.Verify("not-a-key").Should().Be(ApiKeyVerification.Invalid);
    }

    [Fact]
    public void UT051_verify_uses_fixed_time_comparison()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src/Modules/Tenancy/Tenancy.Domain/ApiKey.cs"));
        source.Should().Contain("CryptographicOperations.FixedTimeEquals");
    }

    [Fact]
    public void UT052_revoked_key_is_reported_revoked_even_with_the_right_plaintext()
    {
        var generated = ApiKey.Generate("sdk", "user-1");
        generated.Entity.Revoke(new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc));

        generated.Entity.Verify(generated.Plaintext).Should().Be(ApiKeyVerification.Revoked);
        generated.Entity.RevokedAt.Should().Be(new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("crm_live_ABCDEFGH012345678901234567890123", true, "ABCDEFGH")]
    [InlineData("crm_live_short", false, "")]
    [InlineData("crm_test_ABCDEFGH012345678901234567890123", false, "")]
    [InlineData("crm_live_ABCDEFGH01234567890123456789012!", false, "")]
    [InlineData(null, false, "")]
    public void TryParsePrefix_accepts_only_well_formed_keys(string? plaintext, bool expected, string prefix)
    {
        ApiKey.TryParsePrefix(plaintext, out var actual).Should().Be(expected);
        actual.Should().Be(prefix);
    }

    [Fact]
    public void Touch_records_last_use()
    {
        var generated = ApiKey.Generate("sdk", "user-1");
        var now = new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

        generated.Entity.Touch(now);

        generated.Entity.LastUsedAt.Should().Be(now);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Crm.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Crm.sln not found above the test directory.");
    }
}
```

`tests/Tenancy.UnitTests/RefreshTokenTests.cs`:

```csharp
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public class RefreshTokenTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Issue_returns_a_base64url_secret_and_stores_only_its_hash()
    {
        var issued = RefreshToken.Issue("user-1", T0, TimeSpan.FromDays(7));

        issued.Plaintext.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        issued.Entity.TokenHash.Should().Equal(RefreshToken.HashOf(issued.Plaintext));
        issued.Entity.ExpiresAt.Should().Be(T0.AddDays(7));
        issued.Entity.UserId.Should().Be("user-1");
    }

    [Fact]
    public void IsActive_is_false_after_expiry_or_revocation()
    {
        var issued = RefreshToken.Issue("user-1", T0, TimeSpan.FromDays(7));
        issued.Entity.IsActive(T0.AddDays(6)).Should().BeTrue();
        issued.Entity.IsActive(T0.AddDays(7)).Should().BeFalse();

        var replacement = Guid.CreateVersion7();
        issued.Entity.Revoke(T0.AddHours(1), replacement);

        issued.Entity.IsActive(T0.AddHours(2)).Should().BeFalse();
        issued.Entity.RevokedAt.Should().Be(T0.AddHours(1));
        issued.Entity.ReplacedByTokenId.Should().Be(replacement);
    }

    [Fact]
    public void Revoke_keeps_the_first_revocation()
    {
        var issued = RefreshToken.Issue("user-1", T0, TimeSpan.FromDays(7));
        issued.Entity.Revoke(T0.AddHours(1));
        issued.Entity.Revoke(T0.AddHours(2));

        issued.Entity.RevokedAt.Should().Be(T0.AddHours(1));
    }
}
```

`tests/Tenancy.UnitTests/TenantTests.cs`:

```csharp
using Crm.Kernel;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public class TenantTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_trims_the_name_and_starts_active()
    {
        var result = Tenant.Create("  Demo Inc  ", "demo", T0);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Demo Inc");
        result.Value.Slug.Should().Be("demo");
        result.Value.Status.Should().Be(TenantStatus.Active);
        result.Value.CreatedAt.Should().Be(T0);
        result.Value.Id.Should().NotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("", "demo", "name")]
    [InlineData("Demo", "ab", "slug")]
    [InlineData("Demo", "Demo", "slug")]
    [InlineData("Demo", "has space", "slug")]
    public void Create_rejects_invalid_input_with_field_errors(string name, string slug, string field)
    {
        var result = Tenant.Create(name, slug, T0);

        result.IsFailure.Should().BeTrue();
        result.Error!.Kind.Should().Be(ErrorKind.Validation);
        result.Error.Fields.Should().ContainKey(field);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail to compile**

Run: `dotnet test tests/Tenancy.UnitTests --nologo`
Expected: build error `CS0234`/`CS0246` — namespace `Crm.Tenancy.Domain` has no `ApiKey`, `RefreshToken`, `Tenant`.

- [ ] **Step 3: Implement the domain types**

`src/Modules/Tenancy/Tenancy.Domain/Roles.cs`:

```csharp
namespace Crm.Tenancy.Domain;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";

    public static IReadOnlyList<string> All { get; } = [Admin, Viewer];
}
```

`src/Modules/Tenancy/Tenancy.Domain/Tenant.cs`:

```csharp
using System.Text.RegularExpressions;
using Crm.Kernel;

namespace Crm.Tenancy.Domain;

public enum TenantStatus
{
    Active,
    Suspended,
}

/// <summary>The one aggregate without a TenantId: its Id is the tenant id everywhere else.</summary>
public sealed partial class Tenant
{
    public const int NameMaxLength = 128;

    private Tenant()
    {
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public string Name { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; } = TenantStatus.Active;

    public DateTime CreatedAt { get; private set; }

    [GeneratedRegex("^[a-z0-9-]{3,64}$")]
    private static partial Regex SlugPattern();

    public static Result<Tenant> Create(string name, string slug, DateTime utcNow)
    {
        var fields = new Dictionary<string, string[]>();
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > NameMaxLength)
        {
            fields["name"] = [$"must be 1 to {NameMaxLength} characters"];
        }

        if (slug is null || !SlugPattern().IsMatch(slug))
        {
            fields["slug"] = ["must match [a-z0-9-]{3,64}"];
        }

        if (fields.Count > 0)
        {
            return Error.Validation("tenant.invalid", "The tenant is not valid.", fields);
        }

        return new Tenant { Name = trimmedName, Slug = slug!, CreatedAt = utcNow };
    }
}
```

`src/Modules/Tenancy/Tenancy.Domain/ApiKey.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Crm.Kernel;

namespace Crm.Tenancy.Domain;

public enum ApiKeyVerification
{
    Valid,
    Invalid,
    Revoked,
}

public sealed record GeneratedApiKey(ApiKey Entity, string Plaintext);

/// <summary>Tenant ingest credential. Only the SHA-256 of the key is kept; the plaintext exists once, in <see cref="GeneratedApiKey"/>.</summary>
public sealed class ApiKey : Entity
{
    public const string KeyPrefix = "crm_live_";
    public const int PrefixLength = 8;
    public const int SecretLength = 32;
    public const int NameMaxLength = 128;

    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private ApiKey()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string Prefix { get; private set; } = string.Empty;

    public byte[] KeyHash { get; private set; } = [];

    public string CreatedByUserId { get; private set; } = string.Empty;

    public DateTime? RevokedAt { get; private set; }

    public DateTime? LastUsedAt { get; private set; }

    public static GeneratedApiKey Generate(string name, string createdByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdByUserId);

        var secret = RandomNumberGenerator.GetString(Base62, SecretLength);
        var plaintext = KeyPrefix + secret;
        var entity = new ApiKey
        {
            Name = name.Trim(),
            Prefix = secret[..PrefixLength],
            KeyHash = Hash(plaintext),
            CreatedByUserId = createdByUserId,
        };
        return new GeneratedApiKey(entity, plaintext);
    }

    public static byte[] Hash(string plaintext) => SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

    public static bool TryParsePrefix(string? plaintext, out string prefix)
    {
        prefix = string.Empty;
        if (plaintext is null
            || plaintext.Length != KeyPrefix.Length + SecretLength
            || !plaintext.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var secret = plaintext.AsSpan(KeyPrefix.Length);
        foreach (var c in secret)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        prefix = secret[..PrefixLength].ToString();
        return true;
    }

    public ApiKeyVerification Verify(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!CryptographicOperations.FixedTimeEquals(Hash(plaintext), KeyHash))
        {
            return ApiKeyVerification.Invalid;
        }

        return RevokedAt is null ? ApiKeyVerification.Valid : ApiKeyVerification.Revoked;
    }

    public void Revoke(DateTime utcNow) => RevokedAt ??= utcNow;

    public void Touch(DateTime utcNow) => LastUsedAt = utcNow;
}
```

`src/Modules/Tenancy/Tenancy.Domain/RefreshToken.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Crm.Kernel;

namespace Crm.Tenancy.Domain;

public sealed record IssuedRefreshToken(RefreshToken Entity, string Plaintext);

/// <summary>Opaque rotating refresh token; stored as SHA-256, chained through <see cref="ReplacedByTokenId"/>.</summary>
public sealed class RefreshToken : Entity
{
    public const int SecretBytes = 32;

    private RefreshToken()
    {
    }

    public string UserId { get; private set; } = string.Empty;

    public byte[] TokenHash { get; private set; } = [];

    public DateTime ExpiresAt { get; private set; }

    public DateTime? RevokedAt { get; private set; }

    public Guid? ReplacedByTokenId { get; private set; }

    public static IssuedRefreshToken Issue(string userId, DateTime utcNow, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var plaintext = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));
        var entity = new RefreshToken
        {
            UserId = userId,
            TokenHash = HashOf(plaintext),
            ExpiresAt = utcNow + lifetime,
        };
        return new IssuedRefreshToken(entity, plaintext);
    }

    public static byte[] HashOf(string plaintext) => SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

    public bool IsActive(DateTime utcNow) => RevokedAt is null && utcNow < ExpiresAt;

    public void Revoke(DateTime utcNow, Guid? replacedBy = null)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = utcNow;
        ReplacedByTokenId = replacedBy;
    }
}
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test tests/Tenancy.UnitTests --nologo`
Expected: `Passed!` with 16 tests (ApiKey 11 including theory rows, RefreshToken 3, Tenant 5 → adjust the expected number to what the runner prints and record it in the report; zero failures, zero skipped).

Run: `dotnet build Crm.sln -warnaserror --nologo`
Expected: 0 warnings, 0 errors. `dotnet test tests/Crm.ArchTests --nologo` stays at 13 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Tenancy/Tenancy.Domain tests/Tenancy.UnitTests
git commit -m "feat(tenancy): add tenant, API key, and refresh token domain model

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 2: Identity persistence — `TenancyDbContext`, migration `InitialIdentity`, AT-006

**Files:**
- Modify: `Directory.Packages.props`, `.editorconfig`, `src/Modules/Tenancy/Tenancy.Infrastructure/Tenancy.Infrastructure.csproj`, `src/Modules/Tenancy/Tenancy.Infrastructure/TenancyModule.cs`, `src/Hosts/Crm.Api/Program.cs`, `tests/Crm.ArchTests/TenantScopingTests.cs`, `tests/Crm.IntegrationTests/Fixtures/CrmStackFixture.cs`
- Create: `.config/dotnet-tools.json`, `src/Modules/Tenancy/Tenancy.Infrastructure/AppUser.cs`, `TenancyDbContext.cs`, `TenancyDbContextFactory.cs`, `Configurations/TenantConfiguration.cs`, `Configurations/ApiKeyConfiguration.cs`, `Configurations/RefreshTokenConfiguration.cs`, `Migrations/*` (generated)
- Test: `tests/Crm.ArchTests/TenantScopingTests.cs`, `tests/Crm.IntegrationTests/Tenancy/SchemaTests.cs`

**Interfaces:**
- Consumes: Task 1 domain types; `Crm.Kernel.Data.ITenantScopedDbContext`, `ModelBuilderExtensions.ApplyTenantFilters<TContext>(this ModelBuilder, TContext)` (filters every root type deriving from `Entity`), `TenantSaveChangesInterceptor(ITenantContext, IClock)` (host registers it scoped), `TenantContext` (parameterless ctor).
- Produces:
  - `AppUser : IdentityUser` with `Guid TenantId`, `string? DisplayName`.
  - `TenancyDbContext(DbContextOptions<TenancyDbContext>, ITenantContext)` with `const string Schema = "identity"`, `const string MigrationsHistoryTable = "__EFMigrationsHistory_identity"`, `DbSet<Tenant> Tenants`, `DbSet<ApiKey> ApiKeys`, `DbSet<RefreshToken> RefreshTokens`, plus Identity sets. `AppUser`, `ApiKey`, `RefreshToken` are tenant-filtered; `Tenant` and the Identity tables are not.
  - `AddTenancyModule(this IServiceCollection, IConfiguration)` registers the context and `AddIdentityCore<AppUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<TenancyDbContext>()`.
  - Role rows seeded by the migration: `Admin` id `0192f0a0-0000-7000-8000-000000000001`, `Viewer` id `0192f0a0-0000-7000-8000-000000000002`.
  - `CrmStackFixture` applies Tenancy migrations to `crm_test` during `InitializeAsync`.

- [ ] **Step 1: Packages, project file, tool manifest, editorconfig**

`Directory.Packages.props` — add inside the `<!-- hosting -->` group:

```xml
    <PackageVersion Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.12" />
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.12" />
```

`src/Modules/Tenancy/Tenancy.Infrastructure/Tenancy.Infrastructure.csproj` — the item groups become:

```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Tenancy.Application\Tenancy.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
    <PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
    <PackageReference Include="StackExchange.Redis" />
  </ItemGroup>
```

(`Microsoft.Extensions.Hosting` is dropped: the framework reference supplies it. Keep any `InternalsVisibleTo`/property groups already in the file.)

Tool manifest:

```bash
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.12
```

If `10.0.12` is not published for `dotnet-ef`, install the highest `10.0.x` and record the version in the report. Commit `.config/dotnet-tools.json`.

`.editorconfig` — append (EF scaffolds these files; they are generated code, so style analyzers skip them):

```ini
[**/Migrations/*.cs]
generated_code = true
```

- [ ] **Step 2: Extend AT-006 first (failing test)**

In `tests/Crm.ArchTests/TenantScopingTests.cs`:

1. Add usings `Crm.Tenancy.Domain`, `Crm.Tenancy.Infrastructure`, `Microsoft.AspNetCore.Identity` (keep alphabetical order).
2. In `BuildProvider()` add the config entry `["Auth:SigningKey"] = "arch-test-signing-key-0123456789abcdef0123456789"` and the registration `services.AddTenancyModule(config);` after `AddSegmentationModule`.
3. Replace the `DbContexts` member and the schema assertion, and add one Tenancy-specific fact:

```csharp
    public static TheoryData<Type> DbContexts => new() { typeof(ProfilesDbContext), typeof(SegmentationDbContext), typeof(TenancyDbContext) };

    private static string ExpectedSchema(Type contextType) => contextType.Name switch
    {
        nameof(ProfilesDbContext) => "profiles",
        nameof(SegmentationDbContext) => "segments",
        nameof(TenancyDbContext) => "identity",
        _ => throw new ArgumentOutOfRangeException(nameof(contextType), contextType.Name, "unknown context"),
    };
```

In `AT006_tenant_interceptor_is_registered_and_schema_is_module_owned` replace the two schema lines with `context.Model.GetDefaultSchema().Should().Be(ExpectedSchema(contextType));`.

```csharp
    [Fact]
    public void AT006_identity_context_filters_everything_except_tenants_and_identity_tables()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var filtered = context.Model.GetEntityTypes()
            .Where(t => t.GetDeclaredQueryFilters().Count > 0)
            .Select(t => t.ClrType)
            .ToList();
        var unfiltered = context.Model.GetEntityTypes()
            .Where(t => t.GetDeclaredQueryFilters().Count == 0)
            .Select(t => t.ClrType)
            .ToList();

        filtered.Should().BeEquivalentTo([typeof(AppUser), typeof(ApiKey), typeof(RefreshToken)]);
        unfiltered.Should().BeEquivalentTo(
        [
            typeof(Tenant),
            typeof(IdentityRole),
            typeof(IdentityUserRole<string>),
            typeof(IdentityUserClaim<string>),
            typeof(IdentityUserLogin<string>),
            typeof(IdentityUserToken<string>),
            typeof(IdentityRoleClaim<string>),
        ]);
        context.Model.FindEntityType(typeof(AppUser))!.FindProperty(nameof(AppUser.TenantId)).Should().NotBeNull();
    }
```

`GetDeclaredQueryFilters()` returns a collection in EF Core 10; if its type exposes no `Count`, use `.Any()` / `!.Any()`.

Run: `dotnet test tests/Crm.ArchTests --nologo` → build error: `TenancyDbContext` does not exist.

- [ ] **Step 3: Entity, context, configurations**

`src/Modules/Tenancy/Tenancy.Infrastructure/AppUser.cs`:

```csharp
using Microsoft.AspNetCore.Identity;

namespace Crm.Tenancy.Infrastructure;

/// <summary>Dashboard user. Belongs to exactly one tenant; ids are UUIDv7 strings so <c>sub</c> parses as a Guid.</summary>
public sealed class AppUser : IdentityUser
{
    public AppUser()
    {
        Id = Guid.CreateVersion7().ToString();
    }

    public Guid TenantId { get; set; }

    public string? DisplayName { get; set; }
}
```

`Configurations/TenantConfiguration.cs`:

```csharp
using Crm.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Tenancy.Infrastructure.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(Tenant.NameMaxLength).IsRequired();
        builder.Property(t => t.Slug).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("UX_Tenants_Slug");
    }
}
```

`Configurations/ApiKeyConfiguration.cs`:

```csharp
using Crm.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Tenancy.Infrastructure.Configurations;

internal sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("ApiKeys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).ValueGeneratedNever();
        builder.Property(k => k.Name).HasMaxLength(ApiKey.NameMaxLength).IsRequired();
        builder.Property(k => k.Prefix).HasColumnType("char(8)").IsRequired();
        builder.Property(k => k.KeyHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(k => k.CreatedByUserId).HasMaxLength(450).IsRequired();
        builder.HasIndex(k => k.Prefix).IsUnique().HasDatabaseName("UX_ApiKeys_Prefix");
        builder.HasIndex(k => k.TenantId).HasDatabaseName("IX_ApiKeys_TenantId");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(k => k.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(k => k.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
```

`Configurations/RefreshTokenConfiguration.cs`:

```csharp
using Crm.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crm.Tenancy.Infrastructure.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.UserId).HasMaxLength(450).IsRequired();
        builder.Property(t => t.TokenHash).HasColumnType("binary(32)").IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("UX_RefreshTokens_TokenHash");
        builder.HasIndex(t => t.UserId).HasDatabaseName("IX_RefreshTokens_UserId");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
```

`TenancyDbContext.cs`:

```csharp
using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Tenancy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Crm.Tenancy.Infrastructure;

/// <summary>Schema <c>identity</c>: ASP.NET Core Identity tables plus Tenants, ApiKeys, RefreshTokens.</summary>
public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ITenantContext tenant)
    : IdentityDbContext<AppUser, IdentityRole, string>(options), ITenantScopedDbContext
{
    public const string Schema = "identity";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory_identity";
    public const string AdminRoleId = "0192f0a0-0000-7000-8000-000000000001";
    public const string ViewerRoleId = "0192f0a0-0000-7000-8000-000000000002";

    public Guid CurrentTenantId => tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HavePrecision(3);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema(Schema);
        builder.ApplyConfigurationsFromAssembly(typeof(TenancyDbContext).Assembly);

        builder.Entity<AppUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(128);
            user.HasIndex(u => u.TenantId).HasDatabaseName("IX_AspNetUsers_TenantId");
            user.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);

            // AppUser is not a Kernel Entity, so ApplyTenantFilters skips it; the filter reads this context per query.
            user.HasQueryFilter(u => u.TenantId == CurrentTenantId);
        });

        builder.Entity<IdentityRole>().HasData(
            new IdentityRole { Id = AdminRoleId, Name = Roles.Admin, NormalizedName = "ADMIN", ConcurrencyStamp = AdminRoleId },
            new IdentityRole { Id = ViewerRoleId, Name = Roles.Viewer, NormalizedName = "VIEWER", ConcurrencyStamp = ViewerRoleId });

        builder.ApplyTenantFilters(this);
    }
}
```

`TenancyDbContextFactory.cs`:

```csharp
using Crm.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crm.Tenancy.Infrastructure;

/// <summary>Design-time entry point for <c>dotnet ef</c>; connection string from <c>ConnectionStrings__Crm</c>, else the local compose default.</summary>
public sealed class TenancyDbContextFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    private const string LocalDefault = "Server=localhost,1433;Database=crm;User Id=sa;Password=Crm_Dev_Passw0rd!;TrustServerCertificate=True;Encrypt=False";

    public TenancyDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Crm");
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseSqlServer(
                string.IsNullOrWhiteSpace(connectionString) ? LocalDefault : connectionString,
                sql => sql.MigrationsHistoryTable(TenancyDbContext.MigrationsHistoryTable, TenancyDbContext.Schema))
            .Options;
        return new TenancyDbContext(options, new TenantContext());
    }
}
```

`TenancyModule.cs` (full replacement):

```csharp
using Crm.Kernel.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Tenancy.Infrastructure;

public static class TenancyModule
{
    /// <summary>Registers persistence and ASP.NET Core Identity for the Tenancy module.</summary>
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("Crm");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:Crm is not configured.");
        }

        services.AddDbContext<TenancyDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(TenancyDbContext.MigrationsHistoryTable, TenancyDbContext.Schema))
            .AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));

        services.AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 10;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<TenancyDbContext>();

        return services;
    }
}
```

Run: `dotnet test tests/Crm.ArchTests --nologo` → all pass (13 existing theory rows grow by two `TenancyDbContext` rows plus one fact: expect 16; record the printed number).

- [ ] **Step 4: Generate the migration and add READ_COMMITTED_SNAPSHOT**

```bash
dotnet tool restore
dotnet ef migrations add InitialIdentity \
  --project src/Modules/Tenancy/Tenancy.Infrastructure \
  --startup-project src/Modules/Tenancy/Tenancy.Infrastructure \
  --context TenancyDbContext --output-dir Migrations
```

Open the generated `Migrations/*_InitialIdentity.cs`. As the first statement of `Up`, add (spec §12: the database is created by migrations and the first migration enables snapshot reads, outside the migration transaction):

```csharp
            migrationBuilder.Sql(
                "IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()) = 0 " +
                "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;",
                suppressTransaction: true);
```

Check the scaffold against the data dictionary before moving on: schema `identity` on every table; `Tenants.Slug varchar(64)`; `ApiKeys.Prefix char(8)`, `KeyHash binary(32)`; `datetime2(3)` on `CreatedAt`, `UpdatedAt`, `RevokedAt`, `LastUsedAt`, `ExpiresAt`; the six named indexes (`UX_Tenants_Slug`, `UX_ApiKeys_Prefix`, `IX_ApiKeys_TenantId`, `IX_AspNetUsers_TenantId`, `UX_RefreshTokens_TokenHash`, `IX_RefreshTokens_UserId`); two `InsertData` rows for `AspNetRoles`. Fix the configuration and re-scaffold (`dotnet ef migrations remove`, then `add`) if anything differs; never hand-edit the snapshot.

- [ ] **Step 5: Apply migrations in Development start-up and in the test fixture**

`src/Hosts/Crm.Api/Program.cs` — replace the Development block (ruling P9 removes the magic tenant):

```csharp
        if (app.Environment.IsDevelopment())
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<ProfilesDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<SegmentationDbContext>().Database.MigrateAsync();
        }
```

Remove the now-unused `using Crm.Kernel.Tenancy;` only if nothing else in the file needs it (the `TenantContext` registrations do — keep it).

`tests/Crm.IntegrationTests/Fixtures/CrmStackFixture.cs` — after `await EnsureDatabaseAsync("crm_test");` add `await MigrateTenancyAsync();` and the method (usings: `Crm.Kernel.Tenancy`, `Crm.Tenancy.Infrastructure`, `Microsoft.EntityFrameworkCore`):

```csharp
    /// <summary>Tests run as "Testing", where the API does not auto-migrate; the fixture owns the schema.</summary>
    private async Task MigrateTenancyAsync()
    {
        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseSqlServer(SqlConnectionString, sql => sql.MigrationsHistoryTable(TenancyDbContext.MigrationsHistoryTable, TenancyDbContext.Schema))
            .Options;
        await using var db = new TenancyDbContext(options, new TenantContext());
        await db.Database.MigrateAsync();
    }
```

- [ ] **Step 6: Schema integration test**

`tests/Crm.IntegrationTests/Tenancy/SchemaTests.cs`:

```csharp
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public class SchemaTests(CrmStackFixture stack)
{
    [Fact]
    public async Task Migration_creates_identity_tables_indexes_roles_and_snapshot_isolation()
    {
        await using var connection = new SqlConnection(stack.SqlConnectionString);
        await connection.OpenAsync();

        var indexes = await ReadStringsAsync(connection,
            "SELECT i.name FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'identity' AND i.name IS NOT NULL");
        indexes.Should().Contain(["UX_Tenants_Slug", "UX_ApiKeys_Prefix", "IX_ApiKeys_TenantId", "IX_AspNetUsers_TenantId", "UX_RefreshTokens_TokenHash", "IX_RefreshTokens_UserId"]);

        var roles = await ReadStringsAsync(connection, "SELECT Name FROM [identity].AspNetRoles ORDER BY Name");
        roles.Should().Equal("Admin", "Viewer");

        var snapshot = await ReadStringsAsync(connection, "SELECT CAST(is_read_committed_snapshot_on AS varchar(1)) FROM sys.databases WHERE name = DB_NAME()");
        snapshot.Should().Equal("1");
    }

    private static async Task<List<string>> ReadStringsAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
```

CA2100 (SQL from a variable) fires on `new SqlCommand(sql, …)` when `sql` is not a constant: if it does, inline the three queries as `const string` fields and pass those.

- [ ] **Step 7: Verify**

```bash
dotnet build Crm.sln -warnaserror --nologo
dotnet test Crm.sln --filter "Category!=Integration" --nologo
make test-integration
```

Expected: clean build; unit/arch suites green; integration `Passed: 2` (IT-041 + schema test).

- [ ] **Step 8: Commit**

```bash
git add Directory.Packages.props .editorconfig .config/dotnet-tools.json src/Modules/Tenancy/Tenancy.Infrastructure src/Hosts/Crm.Api/Program.cs tests/Crm.ArchTests tests/Crm.IntegrationTests
git commit -m "feat(tenancy): add identity schema, DbContext, and InitialIdentity migration

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Auth options and token service (UT-053)

**Files:**
- Create: `src/Modules/Tenancy/Tenancy.Application/AuthOptions.cs`, `src/Modules/Tenancy/Tenancy.Application/ITokenService.cs`, `src/Modules/Tenancy/Tenancy.Infrastructure/Tokens/TokenService.cs`
- Modify: `src/Modules/Tenancy/Tenancy.Infrastructure/TenancyModule.cs`, `tests/Tenancy.UnitTests/Tenancy.UnitTests.csproj` (add `ProjectReference` to `Tenancy.Infrastructure`), `src/Hosts/Crm.Api/appsettings.Development.json`, `tests/Crm.ArchTests/TenantScopingTests.cs` only if the `Auth:SigningKey` entry from Task 2 is missing
- Test: `tests/Tenancy.UnitTests/TokenServiceTests.cs`

**Interfaces:**
- Consumes: `Crm.Kernel.Time.IClock`, `Crm.Kernel.Ids.IIdGenerator`, `RefreshToken.Issue` (Task 1).
- Produces:
  - `AuthOptions` (section `Auth`): `string SigningKey`, `string Issuer = "crm"`, `string Audience = "crm-api"`, `int AccessTokenMinutes = 15`, `int RefreshTokenDays = 7`; `TimeSpan AccessTokenLifetime`, `TimeSpan RefreshTokenLifetime`; `const int MinSigningKeyBytes = 32`.
  - `record AccessToken(string Value, int ExpiresInSeconds, DateTime ExpiresAtUtc)`; `ITokenService.IssueAccessToken(string userId, Guid tenantId, IReadOnlyCollection<string> roles) : AccessToken`.
  - `TokenService.CreateValidationParameters(AuthOptions) : TokenValidationParameters` (static; `NameClaimType = "sub"`, `RoleClaimType = "role"`, 30 s clock skew) — Task 5 feeds it to JwtBearer.
  - Claim name constants `TenancyClaims.Subject = "sub"`, `TenantId = "tenant_id"`, `Role = "role"`, `TokenId = "jti"`, `Kind = "kind"`, `KeyPrefix = "key_prefix"`, `KeyId = "key_id"`; kind values `TenancyClaims.KindUser = "user"`, `KindApiKey = "apikey"` (in `ITokenService.cs`'s namespace, file `TenancyClaims.cs` in Application).

- [ ] **Step 1: Failing test**

Add to `tests/Tenancy.UnitTests/Tenancy.UnitTests.csproj`:

```xml
    <ProjectReference Include="..\..\src\Modules\Tenancy\Tenancy.Infrastructure\Tenancy.Infrastructure.csproj" />
```

`tests/Tenancy.UnitTests/TokenServiceTests.cs`:

```csharp
using Crm.Kernel.Ids;
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure.Tokens;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using NSubstitute;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public class TokenServiceTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantId = Guid.Parse("0192f0a0-1111-7000-8000-000000000001");
    private const string UserId = "0192f0a0-2222-7000-8000-000000000002";

    private static readonly AuthOptions Options = new() { SigningKey = "unit-test-signing-key-0123456789abcdef0123456789" };

    private static TokenService CreateService()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(T0);
        return new TokenService(Microsoft.Extensions.Options.Options.Create(Options), clock, new UuidV7IdGenerator());
    }

    [Fact]
    public void UT053_access_token_carries_sub_tenant_role_and_expires_in_15_minutes()
    {
        var token = CreateService().IssueAccessToken(UserId, TenantId, [Roles.Admin]);

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.Value);
        jwt.GetClaim(TenancyClaims.Subject).Value.Should().Be(UserId);
        jwt.GetClaim(TenancyClaims.TenantId).Value.Should().Be(TenantId.ToString());
        jwt.GetClaim(TenancyClaims.Role).Value.Should().Be(Roles.Admin);
        jwt.GetClaim(TenancyClaims.TokenId).Value.Should().NotBeNullOrWhiteSpace();
        jwt.Issuer.Should().Be("crm");
        jwt.Audiences.Should().Equal("crm-api");
        jwt.ValidTo.Should().Be(T0.AddMinutes(15));
        token.ExpiresInSeconds.Should().Be(900);
        token.ExpiresAtUtc.Should().Be(T0.AddMinutes(15));
    }

    [Fact]
    public void UT053_refresh_token_expires_seven_days_after_issue()
    {
        var issued = RefreshToken.Issue(UserId, T0, Options.RefreshTokenLifetime);

        issued.Entity.ExpiresAt.Should().Be(T0.AddDays(7));
    }

    [Fact]
    public async Task Issued_token_validates_with_the_published_parameters_and_fails_with_another_key()
    {
        var token = CreateService().IssueAccessToken(UserId, TenantId, [Roles.Viewer]);

        var parameters = TokenService.CreateValidationParameters(Options);
        parameters.ValidateLifetime = false; // T0 is fixed; lifetime is covered by the ValidTo assertion above
        var valid = await new JsonWebTokenHandler().ValidateTokenAsync(token.Value, parameters);
        valid.IsValid.Should().BeTrue();
        valid.ClaimsIdentity.FindFirst(TenancyClaims.Role)!.Value.Should().Be(Roles.Viewer);

        var other = TokenService.CreateValidationParameters(new AuthOptions { SigningKey = "another-signing-key-0123456789abcdef0123456789ab" });
        other.ValidateLifetime = false;
        (await new JsonWebTokenHandler().ValidateTokenAsync(token.Value, other)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Signing_key_shorter_than_32_bytes_is_rejected()
    {
        var options = new AuthOptions { SigningKey = "too-short" };

        options.IsValid(out var reason).Should().BeFalse();
        reason.Should().Contain("32");
    }
}
```

The unused `using Microsoft.Extensions.Options;` is there for `Options.Create`; because the field is also named `Options`, the call is written fully qualified — drop the using if IDE0005 flags it.

Run: `dotnet test tests/Tenancy.UnitTests --nologo` → build error (types missing).

- [ ] **Step 2: Implement**

`src/Modules/Tenancy/Tenancy.Application/AuthOptions.cs`:

```csharp
using System.Text;

namespace Crm.Tenancy.Application;

/// <summary>Configuration section <c>Auth</c>. HS256 with a configured key in Development; the validation parameters are built from this object only, so RS256 can replace it later.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const int MinSigningKeyBytes = 32;

    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "crm";

    public string Audience { get; set; } = "crm-api";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 7;

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(AccessTokenMinutes);

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(RefreshTokenDays);

    public bool IsValid(out string reason)
    {
        if (Encoding.UTF8.GetByteCount(SigningKey) < MinSigningKeyBytes)
        {
            reason = $"Auth:SigningKey must be at least {MinSigningKeyBytes} bytes.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
        {
            reason = "Auth:Issuer and Auth:Audience are required.";
            return false;
        }

        if (AccessTokenMinutes <= 0 || RefreshTokenDays <= 0)
        {
            reason = "Auth lifetimes must be positive.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
```

`src/Modules/Tenancy/Tenancy.Application/TenancyClaims.cs`:

```csharp
namespace Crm.Tenancy.Application;

public static class TenancyClaims
{
    public const string Subject = "sub";
    public const string TenantId = "tenant_id";
    public const string Role = "role";
    public const string TokenId = "jti";
    public const string Kind = "kind";
    public const string KeyPrefix = "key_prefix";
    public const string KeyId = "key_id";

    public const string KindUser = "user";
    public const string KindApiKey = "apikey";
}
```

`src/Modules/Tenancy/Tenancy.Application/ITokenService.cs`:

```csharp
namespace Crm.Tenancy.Application;

public sealed record AccessToken(string Value, int ExpiresInSeconds, DateTime ExpiresAtUtc);

public interface ITokenService
{
    AccessToken IssueAccessToken(string userId, Guid tenantId, IReadOnlyCollection<string> roles);
}
```

`src/Modules/Tenancy/Tenancy.Infrastructure/Tokens/TokenService.cs`:

```csharp
using System.Text;
using Crm.Kernel.Ids;
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Crm.Tenancy.Infrastructure.Tokens;

public sealed class TokenService(IOptions<AuthOptions> options, IClock clock, IIdGenerator ids) : ITokenService
{
    private readonly AuthOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public static TokenValidationParameters CreateValidationParameters(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = SigningKey(options),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = TenancyClaims.Subject,
            RoleClaimType = TenancyClaims.Role,
        };
    }

    public AccessToken IssueAccessToken(string userId, Guid tenantId, IReadOnlyCollection<string> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(roles);

        var now = clock.UtcNow;
        var expires = now + _options.AccessTokenLifetime;
        var claims = new Dictionary<string, object>
        {
            [TenancyClaims.Subject] = userId,
            [TenancyClaims.TenantId] = tenantId.ToString(),
            [TenancyClaims.TokenId] = ids.NewId().ToString("N"),
            [TenancyClaims.Kind] = TenancyClaims.KindUser,
        };
        if (roles.Count == 1)
        {
            claims[TenancyClaims.Role] = roles.First();
        }
        else if (roles.Count > 1)
        {
            claims[TenancyClaims.Role] = roles.ToArray();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(SigningKey(_options), SecurityAlgorithms.HmacSha256),
        };

        return new AccessToken(_handler.CreateToken(descriptor), (int)_options.AccessTokenLifetime.TotalSeconds, expires);
    }

    private static SymmetricSecurityKey SigningKey(AuthOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey));
}
```

`TenancyModule.AddTenancyModule` — before `return services;` add (usings `Crm.Tenancy.Application`, `Crm.Tenancy.Infrastructure.Tokens`):

```csharp
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .Validate(o => o.IsValid(out _), $"Auth options are invalid: signing key must be at least {AuthOptions.MinSigningKeyBytes} bytes; issuer, audience and lifetimes are required.")
            .ValidateOnStart();
        services.AddSingleton<ITokenService, TokenService>();
```

`src/Hosts/Crm.Api/appsettings.Development.json` — add the section (same dev key as `deploy/.env.example`):

```json
  "Auth": {
    "SigningKey": "dev-only-signing-key-change-me-0123456789abcdef",
    "Issuer": "crm",
    "Audience": "crm-api"
  }
```

- [ ] **Step 3: Verify and commit**

Run: `dotnet test tests/Tenancy.UnitTests --nologo` → all pass (Task 1 count + 4). `dotnet build Crm.sln -warnaserror --nologo` clean. `dotnet test tests/Crm.ArchTests --nologo` green (AT-001/AT-004 still hold: Infrastructure gained only framework and Identity references).

```bash
git add src/Modules/Tenancy tests/Tenancy.UnitTests src/Hosts/Crm.Api/appsettings.Development.json
git commit -m "feat(tenancy): issue HS256 access tokens from configuration-driven auth options

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Problem+json mapping in `Crm.Api` (UT-056)

**Files:**
- Create: `src/Hosts/Crm.Api/Problems/ResultProblemMapper.cs`, `src/Hosts/Crm.Api/Problems/ErrorResultFilter.cs`, `src/Hosts/Crm.Api/Problems/UnhandledExceptionHandler.cs`
- Modify: `src/Hosts/Crm.Api/Program.cs`
- Test: `tests/Crm.Api.UnitTests/ProblemDetailsMapperTests.cs`

**Interfaces:**
- Consumes: `Crm.Kernel.Error`, `ErrorKind` (`Validation`, `NotFound`, `Conflict`, `Forbidden`, `Unauthorized`, `Unexpected`).
- Produces:
  - `ResultProblemMapper.Map(Error error, string traceId, string? instance = null) : ProblemDetails`; `ResultProblemMapper.StatusFor(ErrorKind) : int`; `ResultProblemMapper.TypeFor(string kindLowercase) : string`; `const string TypeBase = "https://crm.example/problems/"`.
  - `ErrorResultFilter : IEndpointFilter` — contract for every module endpoint: **a handler returns an `IResult` on success and a Kernel `Error` on failure**; the filter, attached once to the `/v1` group, converts the `Error`.
  - `UnhandledExceptionHandler : IExceptionHandler` — logs at Error and writes a 500 problem of type `…/unexpected`.

- [ ] **Step 1: Failing tests**

`tests/Crm.Api.UnitTests/ProblemDetailsMapperTests.cs`:

```csharp
using Crm.Api.Problems;
using Crm.Kernel;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Crm.Api.UnitTests;

public class ProblemDetailsMapperTests
{
    private const string TraceId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    public static TheoryData<Error, int, string> Cases => new()
    {
        { Error.Validation("segment.invalid", "The segment is not valid.", new Dictionary<string, string[]> { ["rules[0].op"] = ["unknown operator"] }), 400, "validation" },
        { Error.NotFound("segment.not_found", "No such segment."), 404, "notfound" },
        { Error.Conflict("segment.duplicate", "Name already used."), 409, "conflict" },
        { Error.Forbidden("auth.forbidden", "Not allowed."), 403, "forbidden" },
        { Error.Unauthorized("auth.invalid", "Invalid credentials."), 401, "unauthorized" },
        { Error.Unexpected("boom", "Unexpected."), 500, "unexpected" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void UT056_error_kinds_map_to_status_type_and_trace_id(Error error, int status, string kind)
    {
        var problem = ResultProblemMapper.Map(error, TraceId, "/v1/segments");

        problem.Status.Should().Be(status);
        problem.Type.Should().Be($"https://crm.example/problems/{kind}");
        problem.Detail.Should().Be(error.Message);
        problem.Instance.Should().Be("/v1/segments");
        problem.Extensions["traceId"].Should().Be(TraceId);
        problem.Extensions["code"].Should().Be(error.Code);
    }

    [Fact]
    public void UT056_validation_carries_errors_keyed_by_field_path()
    {
        var error = Error.Validation("segment.invalid", "The segment is not valid.", new Dictionary<string, string[]> { ["rules[0].op"] = ["unknown operator"] });

        var problem = ResultProblemMapper.Map(error, TraceId);

        var errors = problem.Extensions["errors"].Should().BeAssignableTo<IReadOnlyDictionary<string, string[]>>().Subject;
        errors["rules[0].op"].Should().Equal("unknown operator");
        ResultProblemMapper.Map(Error.NotFound("x", "y"), TraceId).Extensions.Should().NotContainKey("errors");
    }

    [Fact]
    public async Task Filter_turns_an_Error_into_a_problem_result_and_passes_other_results_through()
    {
        var filter = new ErrorResultFilter();
        var context = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());

        var converted = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(Error.Conflict("c", "duplicate")));
        converted.Should().BeOfType<ProblemHttpResult>().Which.StatusCode.Should().Be(409);

        var ok = TypedResults.Ok(1);
        var untouched = await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(ok));
        untouched.Should().BeSameAs(ok);
    }
}
```

Run: `dotnet test tests/Crm.Api.UnitTests --nologo` → build error (namespace `Crm.Api.Problems` missing).

- [ ] **Step 2: Implement**

`src/Hosts/Crm.Api/Problems/ResultProblemMapper.cs`:

```csharp
using Crm.Kernel;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Api.Problems;

/// <summary>Kernel <see cref="Error"/> → RFC 9457 problem details. Status, type, title and <c>errors</c> are decided here and nowhere else.</summary>
public static class ResultProblemMapper
{
    public const string TypeBase = "https://crm.example/problems/";

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static string TypeFor(string kindLowercase) => TypeBase + kindLowercase;

    public static ProblemDetails Map(Error error, string traceId, string? instance = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var problem = new ProblemDetails
        {
            Status = StatusFor(error.Kind),
            Type = TypeFor(KindSlug(error.Kind)),
            Title = TitleFor(error.Kind),
            Detail = error.Message,
            Instance = instance,
        };
        problem.Extensions["traceId"] = traceId;
        problem.Extensions["code"] = error.Code;
        if (error.Kind == ErrorKind.Validation)
        {
            problem.Extensions["errors"] = error.Fields ?? new Dictionary<string, string[]>();
        }

        return problem;
    }

    private static string KindSlug(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "validation",
        ErrorKind.Unauthorized => "unauthorized",
        ErrorKind.Forbidden => "forbidden",
        ErrorKind.NotFound => "notfound",
        ErrorKind.Conflict => "conflict",
        _ => "unexpected",
    };

    private static string TitleFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "One or more validation errors occurred.",
        ErrorKind.Unauthorized => "Authentication is required.",
        ErrorKind.Forbidden => "The caller is not allowed to do this.",
        ErrorKind.NotFound => "The resource was not found.",
        ErrorKind.Conflict => "The request conflicts with the current state.",
        _ => "An unexpected error occurred.",
    };
}
```

`src/Hosts/Crm.Api/Problems/ErrorResultFilter.cs`:

```csharp
using System.Diagnostics;
using Crm.Kernel;

namespace Crm.Api.Problems;

/// <summary>Module handlers return an <see cref="IResult"/> or a Kernel <see cref="Error"/>; this filter renders the latter as problem+json.</summary>
public sealed class ErrorResultFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var result = await next(context);
        if (result is not Error error)
        {
            return result;
        }

        var http = context.HttpContext;
        var traceId = Activity.Current?.Id ?? http.TraceIdentifier;
        return Results.Problem(ResultProblemMapper.Map(error, traceId, http.Request.Path));
    }
}
```

`src/Hosts/Crm.Api/Problems/UnhandledExceptionHandler.cs`:

```csharp
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Crm.Api.Problems;

/// <summary>Last line of defence: log the exception (IT-032 relies on TenantContextMissingException surfacing here) and answer 500 problem+json.</summary>
public sealed partial class UnhandledExceptionHandler(IProblemDetailsService problems, ILogger<UnhandledExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        LogUnhandled(logger, exception, exception.GetType().Name, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Type = ResultProblemMapper.TypeFor("unexpected"),
                Title = "An unexpected error occurred.",
                Instance = httpContext.Request.Path,
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled {ExceptionType} on {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string exceptionType, string path);
}
```

`traceId` is added by the `CustomizeProblemDetails` callback already registered in `Program.cs`; the exception message is deliberately not echoed to the client.

`src/Hosts/Crm.Api/Program.cs`:
- after `AddProblemDetails(...)` add `builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();` (using `Crm.Api.Problems`);
- replace the two `v1` lines with:

```csharp
        var v1 = app.MapGroup("/v1").AddEndpointFilter<ErrorResultFilter>();
        _ = v1; // Tenancy endpoints attach in Tasks 5–8
```

- [ ] **Step 3: Verify and commit**

Run: `dotnet test tests/Crm.Api.UnitTests --nologo` → 7 existing + 8 new (6 theory rows + 2 facts) = 15 passed; record the printed number. `dotnet build Crm.sln -warnaserror --nologo` clean.

```bash
git add src/Hosts/Crm.Api tests/Crm.Api.UnitTests
git commit -m "feat(api): map kernel errors and unhandled exceptions to problem+json

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: JWT authentication, tenant resolution, role policies, fail-closed isolation (IT-032)

**Files:**
- Create: `src/Modules/Tenancy/Tenancy.Infrastructure/Auth/AuthSchemes.cs`, `Auth/TenantResolutionMiddleware.cs`, `Endpoints/TestProbeEndpoints.cs`, `Endpoints/TenancyEndpointExtensions.cs`, `src/Hosts/Crm.Api/Problems/ProblemDefaults.cs`, `tests/Crm.IntegrationTests/Fixtures/TenancySeed.cs`
- Modify: `src/Modules/Tenancy/Tenancy.Infrastructure/TenancyModule.cs`, `src/Hosts/Crm.Api/Program.cs`, `tests/Crm.IntegrationTests/Fixtures/CrmApiFactory.cs`
- Test: `tests/Crm.IntegrationTests/Tenancy/IsolationTests.cs`, `tests/Crm.Api.UnitTests/ProblemDefaultsTests.cs`

**Interfaces:**
- Consumes: `TokenService.CreateValidationParameters(AuthOptions)`, `TenancyClaims.*`, `ITokenService` (Task 3); `TenancyDbContext` (Task 2); `ErrorResultFilter` on the `/v1` group (Task 4); Kernel `TenantContext.Resolve(Guid, Guid?, TenantPrincipalKind)`.
- Produces:
  - `AuthSchemes.Bearer = "Bearer"`, `AuthSchemes.ApiKey = "ApiKey"`, `AuthSchemes.Smart = "CrmSmart"`, `AuthSchemes.ApiKeyHeader = "X-Api-Key"`; `AuthPolicies.AdminOnly`, `AuthPolicies.AdminOrViewer`, `AuthPolicies.IngestOnly`; `RateLimitPolicies.Ingest = "ingest"`; `EndpointTags.Ingest = "Ingest"`, `EndpointTags.Tenancy = "Tenancy"`.
  - `app.UseTenantResolution()` (`IApplicationBuilder` extension) — must sit between `UseAuthentication()` and `UseAuthorization()`.
  - `v1.MapTenancyTestProbes()` (`RouteGroupBuilder` extension; Testing only): `GET /_test/read-probe` (AdminOrViewer, returns `{ count }` of the caller tenant's API keys), `POST /_test/write-probe` (AdminOnly, 200).
  - `ProblemDefaults.Apply(ProblemDetailsContext)` — `traceId`, `instance`, and a `crm.example` `type` for status-code responses (401/403/404/429…).
  - Test harness: `TenancySeed.CreateTenantAsync(CrmApiFactory, string? slug = null) : Task<SeededTenant>`, `TenancySeed.AccessToken(CrmApiFactory, string userId, Guid tenantId, params string[] roles) : string`, `TenancySeed.TokenWithoutTenant(CrmApiFactory, string userId, string role) : string`, `TenancySeed.AddApiKeyAsync(CrmApiFactory, SeededTenant, string name = "sdk") : Task<SeededApiKey>`; records `SeededTenant(Guid TenantId, string Slug, string AdminUserId, string AdminEmail, string ViewerUserId, string ViewerEmail)`, `SeededApiKey(Guid Id, string Prefix, string Plaintext)`; `TenancySeed.Password = "Test-Passw0rd!"`. `CrmApiFactory.CreateApiClient()` returns an `HttpClient` with `HandleCookies = false`, `AllowAutoRedirect = false`.

- [ ] **Step 1: Test harness**

`tests/Crm.IntegrationTests/Fixtures/CrmApiFactory.cs` — in the constructor add the signing key next to the three existing variables (same eager-read reason documented there), mirror it in the in-memory collection, and add the client helper:

```csharp
        Environment.SetEnvironmentVariable("Auth__SigningKey", SigningKey);
```

```csharp
    public const string SigningKey = "integration-test-signing-key-0123456789abcdef0123";

    /// <summary>Cookies are asserted and replayed by hand: the refresh cookie is Secure and the test server speaks plain HTTP.</summary>
    public HttpClient CreateApiClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
```

and `["Auth:SigningKey"] = SigningKey,` in the dictionary passed to `AddInMemoryCollection`.

`tests/Crm.IntegrationTests/Fixtures/TenancySeed.cs`:

```csharp
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Crm.IntegrationTests.Fixtures;

public sealed record SeededTenant(Guid TenantId, string Slug, string AdminUserId, string AdminEmail, string ViewerUserId, string ViewerEmail);

public sealed record SeededApiKey(Guid Id, string Prefix, string Plaintext);

/// <summary>Arranges tenants, users, keys and tokens through the API's own services, so tests stay independent of the endpoints under test.</summary>
public static class TenancySeed
{
    public const string Password = "Test-Passw0rd!";

    public static async Task<SeededTenant> CreateTenantAsync(CrmApiFactory factory, string? slug = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        slug ??= "t-" + Guid.CreateVersion7().ToString("N")[..20];

        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<TenancyDbContext>();
        var clock = services.GetRequiredService<IClock>();

        var tenant = Tenant.Create($"Tenant {slug}", slug, clock.UtcNow).Value;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        services.GetRequiredService<TenantContext>().Resolve(tenant.Id, null, TenantPrincipalKind.None);
        var users = services.GetRequiredService<UserManager<AppUser>>();
        var admin = await CreateUserAsync(users, tenant.Id, $"admin@{slug}.test", Roles.Admin);
        var viewer = await CreateUserAsync(users, tenant.Id, $"viewer@{slug}.test", Roles.Viewer);
        return new SeededTenant(tenant.Id, slug, admin.Id, admin.Email!, viewer.Id, viewer.Email!);
    }

    public static async Task<SeededApiKey> AddApiKeyAsync(CrmApiFactory factory, SeededTenant tenant, string name = "sdk")
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(tenant);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(tenant.TenantId, null, TenantPrincipalKind.None);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var generated = ApiKey.Generate(name, tenant.AdminUserId);
        db.ApiKeys.Add(generated.Entity);
        await db.SaveChangesAsync();
        return new SeededApiKey(generated.Entity.Id, generated.Entity.Prefix, generated.Plaintext);
    }

    public static string AccessToken(CrmApiFactory factory, string userId, Guid tenantId, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(userId, tenantId, roles).Value;
    }

    /// <summary>A correctly signed JWT that lacks <c>tenant_id</c> (IT-032).</summary>
    public static string TokenWithoutTenant(CrmApiFactory factory, string userId, string role)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var options = factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object> { [TenancyClaims.Subject] = userId, [TenancyClaims.Role] = role },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(options.SigningKey)), SecurityAlgorithms.HmacSha256),
        });
    }

    private static async Task<AppUser> CreateUserAsync(UserManager<AppUser> users, Guid tenantId, string email, string role)
    {
        var user = new AppUser { TenantId = tenantId, UserName = email, Email = email, EmailConfirmed = true };
        Ensure(await users.CreateAsync(user, Password));
        Ensure(await users.AddToRoleAsync(user, role));
        return user;
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}")));
        }
    }
}
```

- [ ] **Step 2: Failing tests**

`tests/Crm.IntegrationTests/Tenancy/IsolationTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.Tenancy.Domain;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class IsolationTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly CrmApiFactory _factory = new(stack);
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT032_request_without_credentials_is_401_problem_json()
    {
        using var response = await _client.GetAsync("/v1/_test/read-probe");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("type").GetString().Should().Be("https://crm.example/problems/unauthorized");
        body.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task IT032_jwt_without_tenant_id_fails_closed_with_500_and_trace_id()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/_test/read-probe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TenancySeed.TokenWithoutTenant(_factory, tenant.AdminUserId, Roles.Admin));

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("type").GetString().Should().Be("https://crm.example/problems/unexpected");
        body.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        text.Should().NotContain("count", "no rows may be returned without a tenant");
    }

    [Fact]
    public async Task IT032_each_tenant_sees_only_its_own_rows()
    {
        var a = await TenancySeed.CreateTenantAsync(_factory);
        var b = await TenancySeed.CreateTenantAsync(_factory);
        await TenancySeed.AddApiKeyAsync(_factory, b);

        (await ReadCountAsync(a)).Should().Be(0);
        (await ReadCountAsync(b)).Should().Be(1);
    }

    private async Task<int> ReadCountAsync(SeededTenant tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/_test/read-probe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer));
        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("count").GetInt32();
    }
}
```

The spec also asks for a logged `TenantContextMissingException`; Serilog replaces the logging providers in this host, so the test asserts the response and `UnhandledExceptionHandler` (Task 4) owns the log line. Confirm it by eye once in the test output (`Unhandled TenantContextMissingException on /v1/_test/read-probe`) and quote the line in the report.

`tests/Crm.Api.UnitTests/ProblemDefaultsTests.cs`:

```csharp
using Crm.Api.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Crm.Api.UnitTests;

public class ProblemDefaultsTests
{
    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "notfound")]
    [InlineData(429, "ratelimited")]
    [InlineData(500, "unexpected")]
    public void Status_code_problems_get_a_crm_type_trace_id_and_instance(int status, string slug)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/v1/api-keys";
        var context = new ProblemDetailsContext { HttpContext = http, ProblemDetails = new ProblemDetails { Status = status, Type = "https://tools.ietf.org/html/rfc9110" } };

        ProblemDefaults.Apply(context);

        context.ProblemDetails.Type.Should().Be($"https://crm.example/problems/{slug}");
        context.ProblemDetails.Instance.Should().Be("/v1/api-keys");
        context.ProblemDetails.Extensions["traceId"].Should().NotBeNull();
    }

    [Fact]
    public void A_type_set_by_the_mapper_is_kept()
    {
        var context = new ProblemDetailsContext
        {
            HttpContext = new DefaultHttpContext(),
            ProblemDetails = new ProblemDetails { Status = 409, Type = ResultProblemMapper.TypeFor("conflict") },
        };

        ProblemDefaults.Apply(context);

        context.ProblemDetails.Type.Should().Be("https://crm.example/problems/conflict");
    }
}
```

Run both projects → build errors (missing types/endpoints).

- [ ] **Step 3: Implement the module side**

`src/Modules/Tenancy/Tenancy.Infrastructure/Auth/AuthSchemes.cs`:

```csharp
namespace Crm.Tenancy.Infrastructure.Auth;

public static class AuthSchemes
{
    public const string Bearer = "Bearer";
    public const string ApiKey = "ApiKey";
    public const string Smart = "CrmSmart";
    public const string ApiKeyHeader = "X-Api-Key";
}

public static class AuthPolicies
{
    public const string AdminOnly = "AdminOnly";
    public const string AdminOrViewer = "AdminOrViewer";
    public const string IngestOnly = "IngestOnly";
}

public static class RateLimitPolicies
{
    public const string Ingest = "ingest";
}

public static class EndpointTags
{
    public const string Ingest = "Ingest";
    public const string Tenancy = "Tenancy";
}
```

`Auth/TenantResolutionMiddleware.cs`:

```csharp
using Crm.Kernel.Tenancy;
using Crm.Tenancy.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Crm.Tenancy.Infrastructure.Auth;

/// <summary>Copies the authenticated principal's tenant into the scoped <see cref="TenantContext"/>. A principal without <c>tenant_id</c> leaves it unresolved, so any data access fails closed.</summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, ILogger<TenantResolutionMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(logger);

        var user = context.User;
        if (user.Identity?.IsAuthenticated != true
            || !Guid.TryParse(user.FindFirst(TenancyClaims.TenantId)?.Value, out var tenantId)
            || tenantId == Guid.Empty)
        {
            await next(context);
            return;
        }

        var isApiKey = user.FindFirst(TenancyClaims.Kind)?.Value == TenancyClaims.KindApiKey;
        Guid? userId = !isApiKey && Guid.TryParse(user.FindFirst(TenancyClaims.Subject)?.Value, out var parsed) ? parsed : null;
        tenant.Resolve(tenantId, userId, isApiKey ? TenantPrincipalKind.ApiKey : TenantPrincipalKind.User);

        using (logger.BeginScope(new Dictionary<string, object?> { ["TenantId"] = tenantId, ["UserId"] = userId }))
        {
            await next(context);
        }
    }
}
```

`Endpoints/TestProbeEndpoints.cs`:

```csharp
using Crm.Tenancy.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Crm.Tenancy.Infrastructure.Endpoints;

/// <summary>Testing-only stand-ins for endpoints of later plans (plan ruling P4). Deleted when /v1/track and /v1/segments exist.</summary>
internal static class TestProbeEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        var probes = v1.MapGroup("/_test").ExcludeFromDescription();

        probes.MapGet("/read-probe", async (TenancyDbContext db, CancellationToken ct) =>
                TypedResults.Ok(new ProbeCount(await db.ApiKeys.CountAsync(ct))))
            .RequireAuthorization(AuthPolicies.AdminOrViewer);

        probes.MapPost("/write-probe", () => TypedResults.Ok())
            .RequireAuthorization(AuthPolicies.AdminOnly);
    }

    internal sealed record ProbeCount(int Count);
}
```

`Endpoints/TenancyEndpointExtensions.cs`:

```csharp
using Crm.Tenancy.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Crm.Tenancy.Infrastructure.Endpoints;

public static class TenancyEndpointExtensions
{
    /// <summary>After UseAuthentication, before UseRateLimiter/UseAuthorization.</summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantResolutionMiddleware>();
    }

    /// <summary>Testing environment only.</summary>
    public static RouteGroupBuilder MapTenancyTestProbes(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        TestProbeEndpoints.Map(v1);
        return v1;
    }
}
```

`TenancyModule.AddTenancyModule` — add before `return services;` (usings: `Crm.Tenancy.Domain`, `Crm.Tenancy.Infrastructure.Auth`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.Extensions.Options`):

```csharp
        services.AddAuthentication(AuthSchemes.Bearer).AddJwtBearer(AuthSchemes.Bearer, _ => { });
        services.AddOptions<JwtBearerOptions>(AuthSchemes.Bearer)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = TokenService.CreateValidationParameters(auth.Value);
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.AdminOnly, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin))
            .AddPolicy(AuthPolicies.AdminOrViewer, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin, Roles.Viewer))
            .AddPolicy(AuthPolicies.IngestOnly, p => p.RequireAuthenticatedUser().RequireClaim(TenancyClaims.Kind, TenancyClaims.KindApiKey));
```

`AuthSchemes.Bearer` equals `JwtBearerDefaults.AuthenticationScheme`; the constant keeps callers free of the JwtBearer namespace.

- [ ] **Step 4: Implement the host side**

`src/Hosts/Crm.Api/Problems/ProblemDefaults.cs`:

```csharp
using System.Diagnostics;

namespace Crm.Api.Problems;

/// <summary>Applied to every problem response, including the bodiless 401/403/404/429 that UseStatusCodePages fills in.</summary>
public static class ProblemDefaults
{
    public static void Apply(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var problem = context.ProblemDetails;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        problem.Instance ??= context.HttpContext.Request.Path;
        if (problem.Type is null || !problem.Type.StartsWith(ResultProblemMapper.TypeBase, StringComparison.Ordinal))
        {
            problem.Type = ResultProblemMapper.TypeFor(SlugFor(problem.Status ?? context.HttpContext.Response.StatusCode));
        }
    }

    private static string SlugFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "validation",
        StatusCodes.Status401Unauthorized => "unauthorized",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "notfound",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status429TooManyRequests => "ratelimited",
        StatusCodes.Status503ServiceUnavailable => "unavailable",
        _ => "unexpected",
    };
}
```

`src/Hosts/Crm.Api/Program.cs`:

```csharp
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDefaults.Apply);
```

(drop `using System.Diagnostics;` if nothing else uses it) and the pipeline, in spec §6.2 order:

```csharp
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseForwardedHeaders();
        app.UseSerilogRequestLogging();
        app.UseAuthentication();
        app.UseTenantResolution();
        app.UseAuthorization();
```

Register forwarded headers next to the other services (nginx in compose sets them; the default known-proxy list keeps spoofed headers from elsewhere ignored):

```csharp
        builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
```

(usings `Microsoft.AspNetCore.HttpOverrides`, `Crm.Tenancy.Infrastructure.Endpoints`). Endpoint mapping:

```csharp
        var v1 = app.MapGroup("/v1").AddEndpointFilter<ErrorResultFilter>();
        if (app.Environment.IsEnvironment("Testing"))
        {
            v1.MapTenancyTestProbes();
        }
```

Health, metrics and OpenAPI endpoints stay anonymous: none of them calls `RequireAuthorization`, and no fallback policy is set.

- [ ] **Step 5: Verify and commit**

```bash
dotnet build Crm.sln -warnaserror --nologo
dotnet test Crm.sln --filter "Category!=Integration" --nologo
make test-integration
```

Expected: clean build; unit suites green (Api unit +6); integration `Passed: 5` (IT-041, schema, three IT-032 facts). If the 401 body is empty, `UseStatusCodePages` is not seeing the challenge — confirm it is registered before `UseAuthentication` and that `AddProblemDetails` is present.

```bash
git add src/Modules/Tenancy src/Hosts/Crm.Api tests/Crm.IntegrationTests tests/Crm.Api.UnitTests
git commit -m "feat(tenancy): authenticate JWTs, resolve the tenant per request, and fail closed without one

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: API-key authentication and the `ingest` rate limiter (IT-033, IT-035)

**Files:**
- Create: `src/Modules/Tenancy/Tenancy.Infrastructure/Auth/ApiKeyUsageTracker.cs`, `Auth/ApiKeyAuthenticator.cs`, `Auth/ApiKeyAuthenticationHandler.cs`
- Modify: `src/Modules/Tenancy/Tenancy.Infrastructure/TenancyModule.cs`, `Endpoints/TestProbeEndpoints.cs`, `src/Hosts/Crm.Api/Program.cs`
- Test: `tests/Tenancy.UnitTests/ApiKeyUsageTrackerTests.cs`, `tests/Crm.IntegrationTests/Tenancy/ApiKeyTests.cs`, `tests/Crm.IntegrationTests/Tenancy/RateLimitTests.cs`

**Interfaces:**
- Consumes: `ApiKey.TryParsePrefix`, `ApiKey.Hash`, `ApiKey.Verify` (Task 1); `TenancyDbContext` (Task 2); `TenancyClaims`, `AuthSchemes`, `AuthPolicies`, `RateLimitPolicies`, `EndpointTags`, `TenancySeed.AddApiKeyAsync` (Tasks 3, 5); `Crm.Kernel.Redis.RedisKeys.ApiKey(prefix)`; `StackExchange.Redis.IConnectionMultiplexer` (registered by the host through `AddCrmHealthChecks`).
- Produces:
  - `record ApiKeyIdentity(Guid TenantId, Guid KeyId, string Prefix)`; `IApiKeyAuthenticator.AuthenticateAsync(string? plaintext, CancellationToken) : Task<ApiKeyIdentity?>`; `IApiKeyAuthenticator.EvictAsync(string prefix) : Task` (Task 8 calls it on revoke).
  - Cache entry `apikey:{prefix}` = `"{tenantId:N}:{keyId:N}:{keyHashHex}"`, TTL 10 min (ruling P2).
  - Default authentication scheme `AuthSchemes.Smart`: `Authorization: Bearer` → JWT; otherwise `X-Api-Key` → API key; otherwise JWT (challenge 401).
  - Rate-limit policy `ingest`; `POST /v1/_test/ingest-probe` (Testing only; IngestOnly + `ingest`; 202).

- [ ] **Step 1: Failing tests**

`tests/Tenancy.UnitTests/ApiKeyUsageTrackerTests.cs`:

```csharp
using Crm.Tenancy.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace Crm.Tenancy.UnitTests;

public class ApiKeyUsageTrackerTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_prefix_is_touched_at_most_once_per_minute()
    {
        var tracker = new ApiKeyUsageTracker();

        tracker.ShouldTouch("ABCDEFGH", T0).Should().BeTrue();
        tracker.ShouldTouch("ABCDEFGH", T0.AddSeconds(59)).Should().BeFalse();
        tracker.ShouldTouch("ABCDEFGH", T0.AddSeconds(60)).Should().BeTrue();
        tracker.ShouldTouch("ZZZZZZZZ", T0.AddSeconds(1)).Should().BeTrue("prefixes are tracked independently");
    }

    [Fact]
    public void Concurrent_callers_get_exactly_one_touch()
    {
        var tracker = new ApiKeyUsageTracker();
        var granted = 0;

        Parallel.For(0, 200, _ =>
        {
            if (tracker.ShouldTouch("ABCDEFGH", T0))
            {
                Interlocked.Increment(ref granted);
            }
        });

        granted.Should().Be(1);
    }
}
```

`tests/Crm.IntegrationTests/Tenancy/ApiKeyTests.cs`:

```csharp
using System.Net;
using Crm.IntegrationTests.Fixtures;
using Crm.Kernel.Tenancy;
using Crm.Tenancy.Infrastructure;
using Crm.Tenancy.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class ApiKeyTests(CrmStackFixture stack) : IAsyncLifetime
{
    private const string IngestProbe = "/v1/_test/ingest-probe";
    private readonly CrmApiFactory _factory = new(stack);
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT033_valid_tampered_and_revoked_keys()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var valid = await TenancySeed.AddApiKeyAsync(_factory, tenant, "valid");
        var revoked = await TenancySeed.AddApiKeyAsync(_factory, tenant, "revoked");
        await RevokeAsync(tenant, revoked.Id);
        var last = valid.Plaintext[^1];
        var tampered = valid.Plaintext[..^1] + (last == 'a' ? 'b' : 'a');

        (await SendAsync(HttpMethod.Post, IngestProbe, valid.Plaintext)).Should().Be(HttpStatusCode.Accepted);
        (await SendAsync(HttpMethod.Post, IngestProbe, tampered)).Should().Be(HttpStatusCode.Unauthorized, "the cache is warm now and must still verify the full key");
        (await SendAsync(HttpMethod.Post, IngestProbe, revoked.Plaintext)).Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, IngestProbe, "crm_live_not-even-a-key")).Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, "/v1/_test/read-probe", valid.Plaintext)).Should().Be(HttpStatusCode.Forbidden, "API keys may call Ingest endpoints only");
    }

    [Fact]
    public async Task IT033_last_used_is_written_once_for_ten_calls_within_a_minute()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);

        (await SendAsync(HttpMethod.Post, IngestProbe, key.Plaintext)).Should().Be(HttpStatusCode.Accepted);
        var first = await LastUsedAtAsync(key.Id);
        first.Should().NotBeNull();

        for (var i = 0; i < 9; i++)
        {
            (await SendAsync(HttpMethod.Post, IngestProbe, key.Plaintext)).Should().Be(HttpStatusCode.Accepted);
        }

        (await LastUsedAtAsync(key.Id)).Should().Be(first);
    }

    [Fact]
    public async Task Evicting_the_cache_makes_a_revocation_effective_immediately()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var key = await TenancySeed.AddApiKeyAsync(_factory, tenant);
        (await SendAsync(HttpMethod.Post, IngestProbe, key.Plaintext)).Should().Be(HttpStatusCode.Accepted);

        await RevokeAsync(tenant, key.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IApiKeyAuthenticator>().EvictAsync(key.Prefix);
        }

        (await SendAsync(HttpMethod.Post, IngestProbe, key.Plaintext)).Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string apiKey)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(AuthSchemes.ApiKeyHeader, apiKey);
        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    private async Task RevokeAsync(SeededTenant tenant, Guid keyId)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(tenant.TenantId, null, TenantPrincipalKind.None);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var key = await db.ApiKeys.SingleAsync(k => k.Id == keyId);
        key.Revoke(DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    private async Task<DateTime?> LastUsedAtAsync(Guid keyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await db.ApiKeys.IgnoreQueryFilters().Where(k => k.Id == keyId).Select(k => k.LastUsedAt).SingleAsync();
    }
}
```

`tests/Crm.IntegrationTests/Tenancy/RateLimitTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Crm.IntegrationTests.Fixtures;
using Crm.Tenancy.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class RateLimitTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly CrmApiFactory _factory = new(stack);
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT035_bucket_of_2000_rejects_the_excess_with_retry_after_and_spares_other_keys()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var noisy = await TenancySeed.AddApiKeyAsync(_factory, tenant, "noisy");
        var quiet = await TenancySeed.AddApiKeyAsync(_factory, tenant, "quiet");
        var outcomes = new ConcurrentBag<(HttpStatusCode Status, bool HasRetryAfter)>();
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(Enumerable.Range(0, 2100), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (_, ct) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/_test/ingest-probe");
            request.Headers.Add(AuthSchemes.ApiKeyHeader, noisy.Plaintext);
            using var response = await _client.SendAsync(request, ct);
            outcomes.Add((response.StatusCode, response.Headers.RetryAfter is not null));
        });

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "the burst must land inside one replenishment period");
        outcomes.Should().NotContain(o => (int)o.Status >= 500);
        outcomes.Count(o => o.Status == HttpStatusCode.TooManyRequests).Should().BeGreaterThanOrEqualTo(100);
        outcomes.Where(o => o.Status == HttpStatusCode.TooManyRequests).Should().OnlyContain(o => o.HasRetryAfter);
        outcomes.Count(o => o.Status == HttpStatusCode.Accepted).Should().BeLessThanOrEqualTo(4000, "one full bucket plus at most one replenishment");

        using var other = new HttpRequestMessage(HttpMethod.Post, "/v1/_test/ingest-probe");
        other.Headers.Add(AuthSchemes.ApiKeyHeader, quiet.Plaintext);
        using var otherResponse = await _client.SendAsync(other);
        otherResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
```

The case says "within 10 s"; against a remote engine each request pays a Redis round trip, so the test asserts the property that matters — the burst fits in one 60 s replenishment period — and the report records the measured duration.

Run → build errors (`ApiKeyUsageTracker`, `IApiKeyAuthenticator` missing).

- [ ] **Step 2: Implement**

`Auth/ApiKeyUsageTracker.cs`:

```csharp
using System.Collections.Concurrent;

namespace Crm.Tenancy.Infrastructure.Auth;

/// <summary>In-memory, per-process throttle: LastUsedAt is written at most once per minute per key prefix.</summary>
public sealed class ApiKeyUsageTracker
{
    private static readonly long Interval = TimeSpan.FromMinutes(1).Ticks;
    private readonly ConcurrentDictionary<string, long> _lastTouch = new(StringComparer.Ordinal);

    public bool ShouldTouch(string prefix, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var now = utcNow.Ticks;
        while (true)
        {
            if (!_lastTouch.TryGetValue(prefix, out var last))
            {
                if (_lastTouch.TryAdd(prefix, now))
                {
                    return true;
                }

                continue;
            }

            if (now - last < Interval)
            {
                return false;
            }

            if (_lastTouch.TryUpdate(prefix, now, last))
            {
                return true;
            }
        }
    }
}
```

`Auth/ApiKeyAuthenticator.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using Crm.Kernel.Redis;
using Crm.Kernel.Time;
using Crm.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Crm.Tenancy.Infrastructure.Auth;

public sealed record ApiKeyIdentity(Guid TenantId, Guid KeyId, string Prefix);

public interface IApiKeyAuthenticator
{
    Task<ApiKeyIdentity?> AuthenticateAsync(string? plaintext, CancellationToken cancellationToken);

    Task EvictAsync(string prefix);
}

/// <summary>Prefix → Redis (10 min) → SQL. The cache stores the key hash, so a hit still verifies the whole key in constant time.</summary>
public sealed partial class ApiKeyAuthenticator(
    TenancyDbContext db,
    IConnectionMultiplexer redis,
    ApiKeyUsageTracker usage,
    IClock clock,
    ILogger<ApiKeyAuthenticator> logger) : IApiKeyAuthenticator
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public async Task<ApiKeyIdentity?> AuthenticateAsync(string? plaintext, CancellationToken cancellationToken)
    {
        if (!ApiKey.TryParsePrefix(plaintext, out var prefix))
        {
            return null;
        }

        var identity = await FromCacheAsync(prefix, plaintext!) ?? await FromDatabaseAsync(prefix, plaintext!, cancellationToken);
        if (identity is null)
        {
            return null;
        }

        var now = clock.UtcNow;
        if (usage.ShouldTouch(prefix, now))
        {
            // ExecuteUpdate bypasses the change tracker: no tenant is resolved yet at authentication time.
            await db.ApiKeys.IgnoreQueryFilters()
                .Where(k => k.Id == identity.KeyId)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }

        return identity;
    }

    public async Task EvictAsync(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        await redis.GetDatabase().KeyDeleteAsync(RedisKeys.ApiKey(prefix));
    }

    private async Task<ApiKeyIdentity?> FromCacheAsync(string prefix, string plaintext)
    {
        RedisValue cached;
        try
        {
            cached = await redis.GetDatabase().StringGetAsync(RedisKeys.ApiKey(prefix));
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(logger, ex);
            return null;
        }

        if (cached.IsNullOrEmpty)
        {
            return null;
        }

        var parts = cached.ToString().Split(':');
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "N", out var tenantId)
            || !Guid.TryParseExact(parts[1], "N", out var keyId))
        {
            return null;
        }

        return CryptographicOperations.FixedTimeEquals(ApiKey.Hash(plaintext), Convert.FromHexString(parts[2]))
            ? new ApiKeyIdentity(tenantId, keyId, prefix)
            : null;
    }

    private async Task<ApiKeyIdentity?> FromDatabaseAsync(string prefix, string plaintext, CancellationToken cancellationToken)
    {
        var key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(k => k.Prefix == prefix, cancellationToken);
        if (key is null || key.Verify(plaintext) != ApiKeyVerification.Valid)
        {
            return null;
        }

        var value = string.Create(CultureInfo.InvariantCulture, $"{key.TenantId:N}:{key.Id:N}:{Convert.ToHexString(key.KeyHash)}");
        try
        {
            await redis.GetDatabase().StringSetAsync(RedisKeys.ApiKey(prefix), value, CacheTtl);
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(logger, ex);
        }

        return new ApiKeyIdentity(key.TenantId, key.Id, prefix);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "API key cache unavailable; falling back to SQL")]
    private static partial void LogCacheUnavailable(ILogger logger, Exception exception);
}
```

A cached entry with a mismatching hash returns `null` without consulting SQL: the prefix is unique, so SQL would hold the same hash.

`Auth/ApiKeyAuthenticationHandler.cs`:

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Crm.Tenancy.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crm.Tenancy.Infrastructure.Auth;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyAuthenticator authenticator) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AuthSchemes.ApiKeyHeader, out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var identity = await authenticator.AuthenticateAsync(header.ToString(), Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail("The API key is invalid or revoked.");
        }

        var claims = new[]
        {
            new Claim(TenancyClaims.TenantId, identity.TenantId.ToString()),
            new Claim(TenancyClaims.Kind, TenancyClaims.KindApiKey),
            new Claim(TenancyClaims.KeyPrefix, identity.Prefix),
            new Claim(TenancyClaims.KeyId, identity.KeyId.ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, TenancyClaims.Subject, TenancyClaims.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
```

`TenancyModule.AddTenancyModule` — replace the `AddAuthentication(...)` line from Task 5 and add the rate limiter (usings: `System.Globalization`, `System.Threading.RateLimiting`, `Microsoft.AspNetCore.Authentication`, `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.RateLimiting`):

```csharp
        services.AddSingleton<ApiKeyUsageTracker>();
        services.AddScoped<IApiKeyAuthenticator, ApiKeyAuthenticator>();

        services.AddAuthentication(AuthSchemes.Smart)
            .AddPolicyScheme(AuthSchemes.Smart, AuthSchemes.Smart, o => o.ForwardDefaultSelector = SelectScheme)
            .AddJwtBearer(AuthSchemes.Bearer, _ => { })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, _ => { });

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : 60;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            limiter.AddPolicy(RateLimitPolicies.Ingest, http =>
            {
                var prefix = http.User.FindFirst(TenancyClaims.KeyPrefix)?.Value;
                return prefix is null
                    ? RateLimitPartition.GetNoLimiter("no-api-key")
                    : RateLimitPartition.GetTokenBucketLimiter(prefix, _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 2000,
                        TokensPerPeriod = 2000,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });
        });
```

and the selector as a private static method of `TenancyModule` (spec §6.2: a JWT wins, then the API key):

```csharp
    private static string SelectScheme(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthSchemes.Bearer;
        }

        return context.Request.Headers.ContainsKey(AuthSchemes.ApiKeyHeader) ? AuthSchemes.ApiKey : AuthSchemes.Bearer;
    }
```

Requests rejected by the limiter have no body; `UseStatusCodePages` turns them into problem+json with type `…/ratelimited` (Task 5's `ProblemDefaults`).

`Endpoints/TestProbeEndpoints.cs` — add inside `Map`:

```csharp
        probes.MapPost("/ingest-probe", () => TypedResults.Accepted((string?)null))
            .RequireAuthorization(AuthPolicies.IngestOnly)
            .RequireRateLimiting(RateLimitPolicies.Ingest)
            .WithTags(EndpointTags.Ingest);
```

`src/Hosts/Crm.Api/Program.cs` — insert `app.UseRateLimiter();` between `app.UseTenantResolution();` and `app.UseAuthorization();`.

- [ ] **Step 3: Verify and commit**

```bash
dotnet build Crm.sln -warnaserror --nologo
dotnet test Crm.sln --filter "Category!=Integration" --nologo
make test-integration
```

Expected: integration `Passed: 9` (5 + three API-key facts + IT-035); record the IT-035 duration and the 202/429 split in the report.

```bash
git add src/Modules/Tenancy src/Hosts/Crm.Api/Program.cs tests/Tenancy.UnitTests tests/Crm.IntegrationTests
git commit -m "feat(tenancy): authenticate API keys through a Redis-backed cache and rate limit ingest

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Login, refresh rotation with reuse detection, logout (IT-036)

**Files:**
- Create: `src/Modules/Tenancy/Tenancy.Application/TenancyErrors.cs`, `src/Modules/Tenancy/Tenancy.Infrastructure/Auth/AuthService.cs`, `Auth/RefreshCookie.cs`, `Endpoints/AuthEndpoints.cs`
- Modify: `src/Modules/Tenancy/Tenancy.Infrastructure/TenancyModule.cs`, `Endpoints/TenancyEndpointExtensions.cs`, `src/Hosts/Crm.Api/Program.cs`
- Test: `tests/Crm.IntegrationTests/Tenancy/AuthTests.cs`

**Interfaces:**
- Consumes: `ITokenService`, `AuthOptions.RefreshTokenLifetime`, `AccessToken` (Task 3); `RefreshToken.Issue/HashOf/IsActive/Revoke` (Task 1); `TenancyDbContext`, `AppUser`, `UserManager<AppUser>` (Task 2); `AuthPolicies`, `EndpointTags`, `TenancySeed`, `CrmApiFactory.CreateApiClient()` (Task 5). Handler contract from Task 4: return `IResult` or Kernel `Error`.
- Produces:
  - `TenancyErrors.InvalidCredentials`, `TenancyErrors.InvalidRefreshToken`, `TenancyErrors.SlugTaken`, `TenancyErrors.EmailTaken`, `TenancyErrors.ApiKeyNotFound`, `TenancyErrors.Validation(IReadOnlyDictionary<string,string[]>)`, `TenancyErrors.IdentityFailure(IEnumerable<(string Code, string Description)>)`.
  - `record AuthSession(AccessToken Access, string RefreshToken, DateTime RefreshExpiresAtUtc)`; `AuthService.LoginAsync(string email, string password, CancellationToken) : Task<Result<AuthSession>>`, `RefreshAsync(string? refreshToken, CancellationToken) : Task<Result<AuthSession>>`, `LogoutAsync(string userId, CancellationToken) : Task`.
  - `RefreshCookie.Name = "crm_refresh"`, `RefreshCookie.Path = "/v1/auth"`, `RefreshCookie.Write(HttpContext, string token, DateTime expiresUtc, bool isDevelopment)`, `RefreshCookie.Clear(HttpContext, bool isDevelopment)`.
  - `v1.MapTenancyEndpoints()` mapping `POST /auth/login`, `POST /auth/refresh`, `POST /auth/logout`; DTOs `LoginRequest(string Email, string Password)`, `TokenResponse(string AccessToken, int ExpiresIn)`.

- [ ] **Step 1: Failing test**

`tests/Crm.IntegrationTests/Tenancy/AuthTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class AuthTests(CrmStackFixture stack) : IAsyncLifetime
{
    private readonly CrmApiFactory _factory = new(stack);
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT036_login_issues_a_jwt_and_a_refresh_cookie_that_rotates_and_detects_reuse()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);

        using var login = await LoginAsync(tenant.AdminEmail, TenancySeed.Password);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var (accessToken, expiresIn) = await ReadTokenAsync(login);
        accessToken.Should().NotBeNullOrWhiteSpace();
        expiresIn.Should().Be(900);
        var setCookie = SetCookie(login).ToLowerInvariant();
        setCookie.Should().StartWith("crm_refresh=").And.Contain("httponly").And.Contain("secure").And.Contain("samesite=strict").And.Contain("path=/v1/auth");
        var firstCookie = CookieValue(login);

        using var probe = new HttpRequestMessage(HttpMethod.Get, "/v1/_test/read-probe");
        probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using (var probeResponse = await _client.SendAsync(probe))
        {
            probeResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the issued JWT authenticates and resolves the tenant");
        }

        using var refresh = await RefreshAsync(firstCookie);
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondCookie = CookieValue(refresh);
        secondCookie.Should().NotBe(firstCookie);
        var old = await FindTokenAsync(firstCookie);
        old.RevokedAt.Should().NotBeNull();
        old.ReplacedByTokenId.Should().Be((await FindTokenAsync(secondCookie)).Id);

        using var reuse = await RefreshAsync(firstCookie);
        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var afterReuse = await RefreshAsync(secondCookie);
        afterReuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "reuse revokes the whole chain for that user");
    }

    [Fact]
    public async Task Login_failures_are_indistinguishable()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);

        using var wrongPassword = await LoginAsync(tenant.AdminEmail, "Wrong-Passw0rd!");
        using var unknownEmail = await LoginAsync("nobody@nowhere.test", TenancySeed.Password);

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownEmail.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await DetailAsync(wrongPassword)).Should().Be(await DetailAsync(unknownEmail));
        wrongPassword.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Login_rejects_an_empty_body_with_field_errors()
    {
        using var response = await _client.PostAsJsonAsync("/v1/auth/login", new { email = "", password = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").TryGetProperty("email", out _).Should().BeTrue();
        body.RootElement.GetProperty("errors").TryGetProperty("password", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_401_and_logout_revokes_the_chain()
    {
        using var noCookie = await _client.PostAsync("/v1/auth/refresh", null);
        noCookie.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        using var login = await LoginAsync(tenant.ViewerEmail, TenancySeed.Password);
        var (accessToken, _) = await ReadTokenAsync(login);
        var cookie = CookieValue(login);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var logoutResponse = await _client.SendAsync(logout);
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        SetCookie(logoutResponse).ToLowerInvariant().Should().StartWith("crm_refresh=;").And.Contain("expires=");

        using var afterLogout = await RefreshAsync(cookie);
        afterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var anonymousLogout = await _client.PostAsync("/v1/auth/logout", null);
        anonymousLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password)
        => _client.PostAsJsonAsync("/v1/auth/login", new { email, password });

    private async Task<HttpResponseMessage> RefreshAsync(string cookieValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/refresh");
        request.Headers.Add("Cookie", $"crm_refresh={cookieValue}");
        return await _client.SendAsync(request);
    }

    private static string SetCookie(HttpResponseMessage response)
        => response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("crm_refresh=", StringComparison.Ordinal));

    private static string CookieValue(HttpResponseMessage response)
        => Uri.UnescapeDataString(SetCookie(response).Split(';')[0]["crm_refresh=".Length..]);

    private static async Task<(string AccessToken, int ExpiresIn)> ReadTokenAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("accessToken").GetString()!, body.RootElement.GetProperty("expiresIn").GetInt32());
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("detail").GetString();
    }

    private async Task<RefreshToken> FindTokenAsync(string cookieValue)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var hash = RefreshToken.HashOf(cookieValue);
        return await db.RefreshTokens.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.TokenHash == hash);
    }
}
```

Run: `make test-integration` → the new facts fail with 404 (endpoints not mapped).

- [ ] **Step 2: Error catalogue**

`src/Modules/Tenancy/Tenancy.Application/TenancyErrors.cs`:

```csharp
using Crm.Kernel;

namespace Crm.Tenancy.Application;

public static class TenancyErrors
{
    /// <summary>One message for unknown email and wrong password (spec §6.4).</summary>
    public static Error InvalidCredentials { get; } = Error.Unauthorized("auth.invalid_credentials", "Email or password is incorrect.");

    public static Error InvalidRefreshToken { get; } = Error.Unauthorized("auth.invalid_refresh_token", "The refresh token is missing, expired, or revoked.");

    public static Error SlugTaken { get; } = Error.Conflict("tenant.slug_taken", "A tenant with this slug already exists.");

    public static Error EmailTaken { get; } = Error.Conflict("user.email_taken", "A user with this email already exists.");

    public static Error ApiKeyNotFound { get; } = Error.NotFound("api_key.not_found", "No such API key.");

    public static Error Validation(IReadOnlyDictionary<string, string[]> fields)
        => Error.Validation("request.invalid", "The request is not valid.", fields);

    public static Error IdentityFailure(IEnumerable<(string Code, string Description)> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var fields = errors
            .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email")
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
        return Validation(fields);
    }
}
```

- [ ] **Step 3: Cookie rules and the auth service**

`Auth/RefreshCookie.cs`:

```csharp
using Microsoft.AspNetCore.Http;

namespace Crm.Tenancy.Infrastructure.Auth;

/// <summary><c>crm_refresh</c>: HttpOnly, Secure, SameSite=Strict, Path=/v1/auth. Secure is relaxed only for plain-HTTP localhost in Development.</summary>
public static class RefreshCookie
{
    public const string Name = "crm_refresh";
    public const string Path = "/v1/auth";

    public static void Write(HttpContext http, string token, DateTime expiresUtc, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(http);
        var options = Options(http, isDevelopment);
        options.Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc));
        http.Response.Cookies.Append(Name, token, options);
    }

    public static void Clear(HttpContext http, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(Name, Options(http, isDevelopment));
    }

    private static CookieOptions Options(HttpContext http, bool isDevelopment)
    {
        var host = http.Request.Host.Host;
        var plainLocalhost = !http.Request.IsHttps
            && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1");
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = !(isDevelopment && plainLocalhost),
            SameSite = SameSiteMode.Strict,
            Path = Path,
            IsEssential = true,
        };
    }
}
```

`Auth/AuthService.cs`:

```csharp
using Crm.Kernel;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crm.Tenancy.Infrastructure.Auth;

public sealed record AuthSession(AccessToken Access, string RefreshToken, DateTime RefreshExpiresAtUtc);

/// <summary>Anonymous flows start without a tenant: the user or token row is found with filters off, then the scope is resolved to that row's tenant before anything is written.</summary>
public sealed class AuthService(
    TenancyDbContext db,
    UserManager<AppUser> users,
    ITokenService tokens,
    TenantContext tenant,
    IOptions<AuthOptions> options,
    IClock clock)
{
    public async Task<Result<AuthSession>> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeEmail(email);
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (user is null || !await users.CheckPasswordAsync(user, password))
        {
            return TenancyErrors.InvalidCredentials;
        }

        var active = await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.Status == TenantStatus.Active, cancellationToken);
        if (!active || !EnterTenant(user))
        {
            return TenancyErrors.InvalidCredentials;
        }

        return await IssueAsync(user, replaces: null, cancellationToken);
    }

    public async Task<Result<AuthSession>> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return TenancyErrors.InvalidRefreshToken;
        }

        var hash = RefreshToken.HashOf(refreshToken);
        var current = await db.RefreshTokens.IgnoreQueryFilters().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (current is null)
        {
            return TenancyErrors.InvalidRefreshToken;
        }

        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.Id == current.UserId, cancellationToken);
        if (user is null || !EnterTenant(user))
        {
            return TenancyErrors.InvalidRefreshToken;
        }

        var now = clock.UtcNow;
        if (!current.IsActive(now))
        {
            if (current.RevokedAt is not null)
            {
                // A revoked token came back: someone else holds a copy. Kill every live token of this user.
                await RevokeAllAsync(user.Id, now, cancellationToken);
            }

            return TenancyErrors.InvalidRefreshToken;
        }

        return await IssueAsync(user, replaces: current, cancellationToken);
    }

    public async Task LogoutAsync(string userId, CancellationToken cancellationToken)
        => await RevokeAllAsync(userId, clock.UtcNow, cancellationToken);

    private bool EnterTenant(AppUser user)
    {
        if (tenant.IsResolved)
        {
            return tenant.TenantId == user.TenantId;
        }

        tenant.Resolve(user.TenantId, Guid.TryParse(user.Id, out var id) ? id : null, TenantPrincipalKind.User);
        return true;
    }

    private async Task<Result<AuthSession>> IssueAsync(AppUser user, RefreshToken? replaces, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var roles = await users.GetRolesAsync(user);
        var issued = RefreshToken.Issue(user.Id, now, options.Value.RefreshTokenLifetime);
        db.RefreshTokens.Add(issued.Entity);
        replaces?.Revoke(now, issued.Entity.Id);
        await db.SaveChangesAsync(cancellationToken);

        var access = tokens.IssueAccessToken(user.Id, user.TenantId, roles.ToArray());
        return new AuthSession(access, issued.Plaintext, issued.Entity.ExpiresAt);
    }

    private async Task RevokeAllAsync(string userId, DateTime now, CancellationToken cancellationToken)
    {
        var live = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var token in live)
        {
            token.Revoke(now);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
```

`RevokeAllAsync` runs with the tenant resolved (by `EnterTenant` or by the JWT on logout), so the tenant filter applies and the save interceptor accepts the updates.

- [ ] **Step 4: Endpoints**

`Endpoints/AuthEndpoints.cs`:

```csharp
using Crm.Kernel;
using Crm.Tenancy.Application;
using Crm.Tenancy.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Crm.Tenancy.Infrastructure.Endpoints;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record TokenResponse(string AccessToken, int ExpiresIn);

internal static class AuthEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        var auth = v1.MapGroup("/auth").WithTags(EndpointTags.Tenancy);

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .WithName("Login")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .WithName("RefreshToken")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", LogoutAsync)
            .RequireAuthorization(AuthPolicies.AdminOrViewer)
            .WithName("Logout")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static async Task<object> LoginAsync(LoginRequest request, AuthService service, HttpContext http, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            fields["email"] = ["is required"];
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            fields["password"] = ["is required"];
        }

        if (fields.Count > 0)
        {
            return TenancyErrors.Validation(fields);
        }

        var result = await service.LoginAsync(request.Email!, request.Password!, cancellationToken);
        return Respond(result, http, environment);
    }

    private static async Task<object> RefreshAsync(AuthService service, HttpContext http, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        var result = await service.RefreshAsync(http.Request.Cookies[RefreshCookie.Name], cancellationToken);
        if (result.IsFailure)
        {
            RefreshCookie.Clear(http, environment.IsDevelopment());
        }

        return Respond(result, http, environment);
    }

    private static async Task<object> LogoutAsync(AuthService service, HttpContext http, IHostEnvironment environment, CancellationToken cancellationToken)
    {
        var userId = http.User.FindFirst(TenancyClaims.Subject)?.Value;
        if (userId is null)
        {
            return TenancyErrors.InvalidCredentials;
        }

        await service.LogoutAsync(userId, cancellationToken);
        RefreshCookie.Clear(http, environment.IsDevelopment());
        return TypedResults.NoContent();
    }

    private static object Respond(Result<AuthSession> result, HttpContext http, IHostEnvironment environment)
    {
        if (result.IsFailure)
        {
            return result.Error!;
        }

        var session = result.Value;
        RefreshCookie.Write(http, session.RefreshToken, session.RefreshExpiresAtUtc, environment.IsDevelopment());
        return TypedResults.Ok(new TokenResponse(session.Access.Value, session.Access.ExpiresInSeconds));
    }
}
```

`Endpoints/TenancyEndpointExtensions.cs` — add:

```csharp
    /// <summary>Tenancy endpoints under the host's /v1 group (the group carries the Error → problem+json filter).</summary>
    public static RouteGroupBuilder MapTenancyEndpoints(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        AuthEndpoints.Map(v1);
        return v1;
    }
```

`TenancyModule.AddTenancyModule` — add `services.AddScoped<AuthService>();`. `Program.cs` — `v1.MapTenancyEndpoints();` right after the group is created.

A JSON body that fails to bind (malformed JSON) is answered by the framework with a 400 problem through `ProblemDefaults`; an empty object binds to `LoginRequest(null, null)` and gets the field errors above.

- [ ] **Step 5: Verify and commit**

```bash
dotnet build Crm.sln -warnaserror --nologo
dotnet test Crm.sln --filter "Category!=Integration" --nologo
make test-integration
```

Expected: integration `Passed: 13` (9 + four auth facts).

```bash
git add src/Modules/Tenancy src/Hosts/Crm.Api/Program.cs tests/Crm.IntegrationTests
git commit -m "feat(tenancy): add login, rotating refresh cookie with reuse detection, and logout

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Tenant bootstrap and API-key management endpoints (IT-034, IT-037, IT-038)

**Files:**
- Create: `src/Modules/Tenancy/Tenancy.Infrastructure/Tenants/TenantBootstrapService.cs`, `Endpoints/TenantEndpoints.cs`, `Endpoints/ApiKeyEndpoints.cs`
- Modify: `src/Modules/Tenancy/Tenancy.Infrastructure/TenancyModule.cs`, `Endpoints/TenancyEndpointExtensions.cs`, `src/Hosts/Crm.Api/Program.cs`
- Test: `tests/Crm.IntegrationTests/Tenancy/BootstrapTests.cs`; extend `tests/Crm.IntegrationTests/Tenancy/ApiKeyTests.cs` (IT-034) and `AuthTests.cs` (IT-037)

**Interfaces:**
- Consumes: `Tenant.Create`, `ApiKey.Generate/Revoke`, `Roles` (Task 1); `TenancyDbContext`, `AppUser`, `UserManager<AppUser>` (Task 2); `TenancyErrors.*` (Task 7); `IApiKeyAuthenticator.EvictAsync` (Task 6); `AuthPolicies.AdminOnly`, `EndpointTags.Tenancy`, `TenancyClaims.Subject`; `CrmApiFactory { EnvironmentName }` (plan A, init-only property, default `"Testing"`).
- Produces:
  - `v1.MapTenancyBootstrap()` — `POST /tenants` (anonymous). The host maps it only in Development (and for OpenAPI export, Task 9).
  - `MapTenancyEndpoints` additionally maps `GET /api-keys`, `POST /api-keys`, `DELETE /api-keys/{id:guid}` (AdminOnly).
  - DTOs: `BootstrapTenantRequest(string? Name, string? Slug, string? AdminEmail, string? AdminPassword, string? ViewerEmail, string? ViewerPassword)`, `TenantCreatedResponse(Guid TenantId, string Slug)`, `CreateApiKeyRequest(string? Name)`, `ApiKeyCreatedResponse(Guid Id, string Name, string Prefix, string Key)`, `ApiKeyListItem(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt)`, `ApiKeyListResponse(IReadOnlyList<ApiKeyListItem> Items, string? NextCursor)` (the list shape of `docs/api/README.md` § Pagination; `NextCursor` is always `null` here).

- [ ] **Step 1: Failing tests**

`tests/Crm.IntegrationTests/Tenancy/BootstrapTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using Crm.Tenancy.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Crm.IntegrationTests.Tenancy;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class BootstrapTests(CrmStackFixture stack)
{
    private static object Body(string slug, bool withViewer = false) => new
    {
        name = "Demo " + slug,
        slug,
        adminEmail = $"admin@{slug}.test",
        adminPassword = TenancySeed.Password,
        viewerEmail = withViewer ? $"viewer@{slug}.test" : null,
        viewerPassword = withViewer ? TenancySeed.Password : null,
    };

    [Fact]
    public async Task IT038_development_bootstraps_a_tenant_whose_admin_can_log_in()
    {
        await using var factory = new CrmApiFactory(stack) { EnvironmentName = "Development" };
        using var client = factory.CreateApiClient();
        var slug = "boot-" + Guid.CreateVersion7().ToString("N")[..12];

        using var created = await client.PostAsJsonAsync("/v1/tenants", Body(slug, withViewer: true));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("slug").GetString().Should().Be(slug);
        body.RootElement.GetProperty("tenantId").GetGuid().Should().NotBe(Guid.Empty);

        using var adminLogin = await client.PostAsJsonAsync("/v1/auth/login", new { email = $"admin@{slug}.test", password = TenancySeed.Password });
        adminLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        adminLogin.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant().Should().NotContain("secure", "Development relaxes Secure for plain-HTTP localhost");
        using var viewerLogin = await client.PostAsJsonAsync("/v1/auth/login", new { email = $"viewer@{slug}.test", password = TenancySeed.Password });
        viewerLogin.StatusCode.Should().Be(HttpStatusCode.OK);

        using var duplicate = await client.PostAsJsonAsync("/v1/tenants", Body(slug));
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task IT038_bootstrap_validates_input_and_writes_nothing_on_failure()
    {
        await using var factory = new CrmApiFactory(stack) { EnvironmentName = "Development" };
        using var client = factory.CreateApiClient();
        var slug = "weak-" + Guid.CreateVersion7().ToString("N")[..12];

        using var badSlug = await client.PostAsJsonAsync("/v1/tenants", new { name = "X", slug = "Bad Slug", adminEmail = "a@b.test", adminPassword = TenancySeed.Password });
        badSlug.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var weakPassword = await client.PostAsJsonAsync("/v1/tenants", new { name = "X", slug, adminEmail = $"admin@{slug}.test", adminPassword = "short" });
        weakPassword.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await weakPassword.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("errors").TryGetProperty("password", out _).Should().BeTrue();
        (await TenantExistsAsync(factory, slug)).Should().BeFalse("the tenant row is rolled back when the admin cannot be created");
    }

    [Fact]
    public async Task IT038_production_does_not_map_the_endpoint()
    {
        await using var factory = new CrmApiFactory(stack) { EnvironmentName = "Production" };
        using var client = factory.CreateApiClient();
        var slug = "prod-" + Guid.CreateVersion7().ToString("N")[..12];

        using var response = await client.PostAsJsonAsync("/v1/tenants", Body(slug));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TenantExistsAsync(factory, slug)).Should().BeFalse();
    }

    private static async Task<bool> TenantExistsAsync(CrmApiFactory factory, string slug)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().Tenants.AnyAsync(t => t.Slug == slug);
    }
}
```

Add to `ApiKeyTests.cs` (usings to add: `System.Net.Http.Headers`, `System.Net.Http.Json`, `System.Text.Json`, `Crm.Tenancy.Domain`):

```csharp
    [Fact]
    public async Task IT034_full_key_is_shown_once_and_revocation_is_immediate()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var other = await TenancySeed.CreateTenantAsync(_factory);
        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);

        using var created = await SendJsonAsync(HttpMethod.Post, "/v1/api-keys", admin, new { name = "sdk" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var key = createdBody.RootElement.GetProperty("key").GetString()!;
        var id = createdBody.RootElement.GetProperty("id").GetGuid();
        key.Should().MatchRegex("^crm_live_[0-9A-Za-z]{32}$");
        createdBody.RootElement.GetProperty("prefix").GetString().Should().Be(key.Substring("crm_live_".Length, 8));

        using var list = await SendJsonAsync(HttpMethod.Get, "/v1/api-keys", admin, null);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var item = listBody.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("prefix").GetString().Should().HaveLength(8);
        item.TryGetProperty("key", out _).Should().BeFalse();
        item.TryGetProperty("keyHash", out _).Should().BeFalse();

        using (var scope = _factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<TenancyDbContext>().ApiKeys.IgnoreQueryFilters().AsNoTracking().SingleAsync(k => k.Id == id);
            row.KeyHash.Should().Equal(ApiKey.Hash(key));
        }

        (await SendAsync(HttpMethod.Post, IngestProbe, key)).Should().Be(HttpStatusCode.Accepted);

        var otherAdmin = TenancySeed.AccessToken(_factory, other.AdminUserId, other.TenantId, Roles.Admin);
        using var foreignDelete = await SendJsonAsync(HttpMethod.Delete, $"/v1/api-keys/{id}", otherAdmin, null);
        foreignDelete.StatusCode.Should().Be(HttpStatusCode.NotFound, "another tenant cannot see the key");

        using var deleted = await SendJsonAsync(HttpMethod.Delete, $"/v1/api-keys/{id}", admin, null);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Post, IngestProbe, key)).Should().Be(HttpStatusCode.Unauthorized, "revoke evicts the cache entry");

        using var blankName = await SendJsonAsync(HttpMethod.Post, "/v1/api-keys", admin, new { name = " " });
        blankName.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string bearer, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }
```

Add to `AuthTests.cs`:

```csharp
    [Fact]
    public async Task IT037_viewer_role_is_read_only()
    {
        var tenant = await TenancySeed.CreateTenantAsync(_factory);
        var viewer = TenancySeed.AccessToken(_factory, tenant.ViewerUserId, tenant.TenantId, Roles.Viewer);

        (await StatusAsync(HttpMethod.Get, "/v1/_test/read-probe", viewer)).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(HttpMethod.Post, "/v1/_test/write-probe", viewer)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(HttpMethod.Post, "/v1/api-keys", viewer)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusAsync(HttpMethod.Get, "/v1/api-keys", viewer)).Should().Be(HttpStatusCode.Forbidden);

        var admin = TenancySeed.AccessToken(_factory, tenant.AdminUserId, tenant.TenantId, Roles.Admin);
        (await StatusAsync(HttpMethod.Post, "/v1/_test/write-probe", admin)).Should().Be(HttpStatusCode.OK);
    }

    private async Task<HttpStatusCode> StatusAsync(HttpMethod method, string path, string bearer)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { name = "sdk" });
        }

        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }
```

Run `make test-integration` → new facts fail with 404.

- [ ] **Step 2: Bootstrap service**

`Tenants/TenantBootstrapService.cs`:

```csharp
using Crm.Kernel;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crm.Tenancy.Infrastructure.Tenants;

public sealed record BootstrapTenantCommand(string Name, string Slug, string AdminEmail, string AdminPassword, string? ViewerEmail, string? ViewerPassword);

/// <summary>Development-only: one transaction creates the tenant, its Admin and, optionally, a Viewer.</summary>
public sealed class TenantBootstrapService(TenancyDbContext db, UserManager<AppUser> users, TenantContext tenantContext, IClock clock)
{
    public async Task<Result<Tenant>> CreateAsync(BootstrapTenantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (tenantContext.IsResolved)
        {
            return Error.Forbidden("tenant.bootstrap_authenticated", "Bootstrap must be called without credentials.");
        }

        var created = Tenant.Create(command.Name, command.Slug, clock.UtcNow);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        if (await db.Tenants.AnyAsync(t => t.Slug == command.Slug, cancellationToken))
        {
            return TenancyErrors.SlugTaken;
        }

        var emails = new[] { command.AdminEmail, command.ViewerEmail }.Where(e => e is not null).Select(e => users.NormalizeEmail(e)).ToList();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => emails.Contains(u.NormalizedEmail), cancellationToken))
        {
            return TenancyErrors.EmailTaken;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tenant = created.Value;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
        tenantContext.Resolve(tenant.Id, null, TenantPrincipalKind.None);

        var failure = await CreateUserAsync(tenant.Id, command.AdminEmail, command.AdminPassword, Roles.Admin);
        if (failure is null && command.ViewerEmail is not null && command.ViewerPassword is not null)
        {
            failure = await CreateUserAsync(tenant.Id, command.ViewerEmail, command.ViewerPassword, Roles.Viewer);
        }

        if (failure is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return failure;
        }

        await transaction.CommitAsync(cancellationToken);
        return tenant;
    }

    private async Task<Error?> CreateUserAsync(Guid tenantId, string email, string password, string role)
    {
        var user = new AppUser { TenantId = tenantId, UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        if (result.Succeeded)
        {
            result = await users.AddToRoleAsync(user, role);
        }

        return result.Succeeded ? null : TenancyErrors.IdentityFailure(result.Errors.Select(e => (e.Code, e.Description)));
    }
}
```

`UserManager` and the endpoint share the scoped `TenancyDbContext`, so Identity's saves join the transaction.

- [ ] **Step 3: Endpoints**

`Endpoints/TenantEndpoints.cs`:

```csharp
using Crm.Tenancy.Application;
using Crm.Tenancy.Infrastructure.Auth;
using Crm.Tenancy.Infrastructure.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Crm.Tenancy.Infrastructure.Endpoints;

public sealed record BootstrapTenantRequest(string? Name, string? Slug, string? AdminEmail, string? AdminPassword, string? ViewerEmail, string? ViewerPassword);

public sealed record TenantCreatedResponse(Guid TenantId, string Slug);

internal static class TenantEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        v1.MapPost("/tenants", CreateAsync)
            .AllowAnonymous()
            .WithTags(EndpointTags.Tenancy)
            .WithName("BootstrapTenant")
            .WithSummary("Development only: bootstrap a tenant with its first Admin (and optionally a Viewer).")
            .Produces<TenantCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<object> CreateAsync(BootstrapTenantRequest request, TenantBootstrapService service, CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.AdminEmail))
        {
            fields["adminEmail"] = ["is required"];
        }

        if (string.IsNullOrEmpty(request.AdminPassword))
        {
            fields["adminPassword"] = ["is required"];
        }

        if (string.IsNullOrWhiteSpace(request.ViewerEmail) != string.IsNullOrEmpty(request.ViewerPassword))
        {
            fields["viewerEmail"] = ["viewerEmail and viewerPassword must be given together"];
        }

        if (fields.Count > 0)
        {
            return TenancyErrors.Validation(fields);
        }

        var result = await service.CreateAsync(
            new BootstrapTenantCommand(
                request.Name ?? string.Empty,
                request.Slug ?? string.Empty,
                request.AdminEmail!,
                request.AdminPassword!,
                string.IsNullOrWhiteSpace(request.ViewerEmail) ? null : request.ViewerEmail,
                string.IsNullOrEmpty(request.ViewerPassword) ? null : request.ViewerPassword),
            cancellationToken);

        return result.IsFailure
            ? result.Error!
            : TypedResults.Created((string?)null, new TenantCreatedResponse(result.Value.Id, result.Value.Slug));
    }
}
```

`Endpoints/ApiKeyEndpoints.cs`:

```csharp
using Crm.Kernel.Time;
using Crm.Tenancy.Application;
using Crm.Tenancy.Domain;
using Crm.Tenancy.Infrastructure.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Crm.Tenancy.Infrastructure.Endpoints;

public sealed record CreateApiKeyRequest(string? Name);

public sealed record ApiKeyCreatedResponse(Guid Id, string Name, string Prefix, string Key);

public sealed record ApiKeyListItem(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

public sealed record ApiKeyListResponse(IReadOnlyList<ApiKeyListItem> Items, string? NextCursor);

internal static class ApiKeyEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        var keys = v1.MapGroup("/api-keys")
            .WithTags(EndpointTags.Tenancy)
            .RequireAuthorization(AuthPolicies.AdminOnly);

        keys.MapGet("/", ListAsync).WithName("ListApiKeys").Produces<ApiKeyListResponse>();

        keys.MapPost("/", CreateAsync)
            .WithName("CreateApiKey")
            .WithSummary("The full key is returned once and cannot be retrieved again.")
            .Produces<ApiKeyCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        keys.MapDelete("/{id:guid}", RevokeAsync)
            .WithName("RevokeApiKey")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<object> ListAsync(TenancyDbContext db, CancellationToken cancellationToken)
    {
        var items = await db.ApiKeys.AsNoTracking()
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyListItem(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new ApiKeyListResponse(items, null));
    }

    private static async Task<object> CreateAsync(CreateApiKeyRequest request, TenancyDbContext db, HttpContext http, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > ApiKey.NameMaxLength)
        {
            return TenancyErrors.Validation(new Dictionary<string, string[]> { ["name"] = [$"must be 1 to {ApiKey.NameMaxLength} characters"] });
        }

        var userId = http.User.FindFirst(TenancyClaims.Subject)?.Value;
        if (userId is null)
        {
            return TenancyErrors.InvalidCredentials;
        }

        var generated = ApiKey.Generate(name, userId);
        db.ApiKeys.Add(generated.Entity);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            (string?)null,
            new ApiKeyCreatedResponse(generated.Entity.Id, generated.Entity.Name, generated.Entity.Prefix, generated.Plaintext));
    }

    private static async Task<object> RevokeAsync(Guid id, TenancyDbContext db, IApiKeyAuthenticator authenticator, IClock clock, CancellationToken cancellationToken)
    {
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (key is null)
        {
            return TenancyErrors.ApiKeyNotFound;
        }

        key.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await authenticator.EvictAsync(key.Prefix);
        return TypedResults.NoContent();
    }
}
```

`TenancyEndpointExtensions` — `MapTenancyEndpoints` also calls `ApiKeyEndpoints.Map(v1);` and add:

```csharp
    /// <summary>Development only (the host also maps it while exporting OpenAPI so the document does not depend on the environment).</summary>
    public static RouteGroupBuilder MapTenancyBootstrap(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        TenantEndpoints.Map(v1);
        return v1;
    }
```

`TenancyModule` — `services.AddScoped<TenantBootstrapService>();` (using `Crm.Tenancy.Infrastructure.Tenants`). `Program.cs`:

```csharp
        if (app.Environment.IsDevelopment())
        {
            v1.MapTenancyBootstrap();
        }
```

- [ ] **Step 4: Verify and commit**

```bash
dotnet build Crm.sln -warnaserror --nologo
dotnet test Crm.sln --filter "Category!=Integration" --nologo
make test-integration
```

Expected: integration `Passed: 18` (13 + three bootstrap facts + IT-034 + IT-037). The Development factory loads `appsettings.Development.json`; the environment variables set by `CrmApiFactory` override its connection settings, so it still talks to the test containers.

```bash
git add src/Modules/Tenancy src/Hosts/Crm.Api/Program.cs tests/Crm.IntegrationTests
git commit -m "feat(tenancy): add development tenant bootstrap and API key management endpoints

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: OpenAPI document — export after mapping, commit it, enforce drift (CT-001)

**Files:**
- Create: `src/Hosts/Crm.Api/OpenApi/CrmDocumentTransformer.cs`, `docs/api/openapi.json` (generated)
- Modify: `src/Hosts/Crm.Api/Program.cs`, `.github/workflows/ci.yml`, `Makefile` (`openapi` target)
- Test: `tests/Crm.Api.UnitTests/OpenApiDocumentTests.cs`

**Interfaces:**
- Consumes: `OpenApiExport.Parse/WriteAsync` (plan A; resolves the keyed `IOpenApiDocumentProvider` `"v1"`), `MapTenancyEndpoints`, `MapTenancyBootstrap`, `MapTenancyTestProbes` (Tasks 5–8; probes call `ExcludeFromDescription`).
- Produces: a deterministic `docs/api/openapi.json` with exactly these operations — `POST /v1/auth/login`, `POST /v1/auth/refresh`, `POST /v1/auth/logout`, `POST /v1/tenants`, `GET /v1/api-keys`, `POST /v1/api-keys`, `DELETE /v1/api-keys/{id}` — plus security schemes `bearer` and `apiKey`; CI fails on drift.

- [ ] **Step 1: Failing test**

`tests/Crm.Api.UnitTests/OpenApiDocumentTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Crm.Api.UnitTests;

/// <summary>CT-001 (plan B scope): the committed document lists exactly the Tenancy operations. Freshness is enforced by the CI drift step.</summary>
public class OpenApiDocumentTests
{
    private static readonly string[] Expected =
    [
        "DELETE /v1/api-keys/{id}",
        "GET /v1/api-keys",
        "POST /v1/api-keys",
        "POST /v1/auth/login",
        "POST /v1/auth/logout",
        "POST /v1/auth/refresh",
        "POST /v1/tenants",
    ];

    [Fact]
    public void CT001_committed_document_lists_exactly_the_tenancy_operations()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs/api/openapi.json")));

        var operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(op => op.Name is "get" or "post" or "put" or "patch" or "delete")
                .Select(op => $"{op.Name.ToUpperInvariant()} {path.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        operations.Should().Equal(Expected);
        document.RootElement.GetProperty("info").GetProperty("title").GetString().Should().Be("CRM Platform API");
        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        schemes.TryGetProperty("bearer", out _).Should().BeTrue();
        schemes.TryGetProperty("apiKey", out _).Should().BeTrue();
        if (document.RootElement.TryGetProperty("servers", out var servers))
        {
            servers.GetArrayLength().Should().Be(0, "the document must not depend on where it was exported");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Crm.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Crm.sln not found above the test directory.");
    }
}
```

Run: `dotnet test tests/Crm.Api.UnitTests --nologo` → fails: `docs/api/openapi.json` not found.

- [ ] **Step 2: Document transformer**

`src/Hosts/Crm.Api/OpenApi/CrmDocumentTransformer.cs`:

```csharp
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Crm.Api.OpenApi;

/// <summary>Stable metadata: title/version, no servers (export must not depend on the machine), and the two credential schemes.</summary>
public sealed class CrmDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Info = new OpenApiInfo
        {
            Title = "CRM Platform API",
            Version = "v1",
            Description = "Multi-tenant CRM: ingestion, profiles, segmentation. Errors are application/problem+json.",
        };
        document.Servers = [];

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Dashboard users: 15-minute access token from POST /v1/auth/login.",
        };
        document.Components.SecuritySchemes["apiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Api-Key",
            Description = "Ingest clients: crm_live_<32 base62>.",
        };
        return Task.CompletedTask;
    }
}
```

This targets `Microsoft.OpenApi` 2.x as shipped with `Microsoft.AspNetCore.OpenApi` 10.0.12 (the namespace plan A's `OpenApiExport` already uses). If a member differs (`IOpenApiSecurityScheme` vs `OpenApiSecurityScheme` as the dictionary value type, or `Servers` being non-nullable `IList`), adapt to the compiled API and keep the three outcomes: fixed `info`, empty `servers`, the two schemes. Note the adaptation in the report.

- [ ] **Step 3: Reorder `Program.Main` — map first, then export**

The export block currently runs right after `builder.Build()`, before any endpoint exists, so the document would be empty. Final shape of the section between `Build()` and `RunAsync()`:

```csharp
        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseForwardedHeaders();
        app.UseSerilogRequestLogging();
        app.UseAuthentication();
        app.UseTenantResolution();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapOpenApi("/openapi/{documentName}.json");
        if (app.Environment.IsDevelopment())
        {
            app.MapScalarApiReference("/swagger", options => options.WithOpenApiRoutePattern("/openapi/{documentName}.json"));
        }

        app.MapCrmHealth();
        app.MapCrmMetrics();

        var v1 = app.MapGroup("/v1").AddEndpointFilter<ErrorResultFilter>();
        v1.MapTenancyEndpoints();
        if (app.Environment.IsDevelopment() || exportPath is not null)
        {
            v1.MapTenancyBootstrap(); // in the exported contract regardless of the exporting environment
        }

        if (app.Environment.IsEnvironment("Testing"))
        {
            v1.MapTenancyTestProbes();
        }

        if (exportPath is not null)
        {
            await OpenApiExport.WriteAsync(app, exportPath);
            Log.Information("OpenAPI document written to {Path}", exportPath);
            return;
        }

        if (app.Environment.IsDevelopment())
        {
            // migrations block from Task 2, unchanged
        }

        await app.RunAsync();
```

and the registration becomes:

```csharp
        builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer<CrmDocumentTransformer>());
```

Health and metrics endpoints must not appear in the document: if the export shows `/health/*` or `/metrics`, add `.ExcludeFromDescription()` to those mappings in `Crm.Hosting` (`MapCrmHealth`, `MapCrmMetrics`).

- [ ] **Step 4: Export, commit the document, make CI strict**

`Makefile` — the `openapi` target (works without a database; the key only has to satisfy option validation):

```make
openapi: ## export docs/api/openapi.json from the API project (CT-001)
	ConnectionStrings__Crm="Server=localhost;Database=crm;User Id=sa;Password=unused;TrustServerCertificate=True" \
	Redis__Configuration="localhost:6379" Kafka__BootstrapServers="localhost:9092" \
	Auth__SigningKey="openapi-export-only-0123456789abcdef0123456789" \
	dotnet run --project src/Hosts/Crm.Api -c Release --no-launch-profile -- --export-openapi docs/api/openapi.json
```

Run `make openapi` twice and confirm `git diff --stat docs/api/openapi.json` is empty after the second run (deterministic output). Inspect the file: seven operations, request/response schemas for the DTOs of Tasks 7–8, `application/problem+json` responses, no `_test` paths.

`.github/workflows/ci.yml` — replace the export step body and add the key:

```yaml
      - name: export OpenAPI and diff against committed document (CT-001)
        run: |
          mkdir -p build
          dotnet run --project src/Hosts/Crm.Api -c Release --no-launch-profile -- --export-openapi build/openapi.json
          diff -u docs/api/openapi.json build/openapi.json
        env:
          ConnectionStrings__Crm: "Server=localhost;Database=crm;User Id=sa;Password=unused;TrustServerCertificate=True"
          Redis__Configuration: "localhost:6379"
          Kafka__BootstrapServers: "localhost:9092"
          Auth__SigningKey: "openapi-export-only-0123456789abcdef0123456789"
```

Verify the CI command locally exactly as written (Production environment): `build/openapi.json` must be byte-identical to the committed file. `build/` is already git-ignored; if not, add it to `.gitignore`.

- [ ] **Step 5: Verify and commit**

```bash
dotnet build Crm.sln -warnaserror --nologo
dotnet test Crm.sln --filter "Category!=Integration" --nologo
make test-integration
```

Expected: `OpenApiDocumentTests` green; existing `OpenApiExportTests` still green; integration count unchanged at 18 (pipeline order did not change behaviour).

```bash
git add src/Hosts/Crm.Api src/Shared/Crm.Hosting tests/Crm.Api.UnitTests docs/api/openapi.json Makefile .github/workflows/ci.yml
git commit -m "feat(api): export the OpenAPI document after endpoint mapping and enforce drift in CI

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: `make migrate`, `make seed`, compose wiring, documentation, exit gate

**Files:**
- Create: `tools/seed.py`
- Modify: `Makefile` (`migrate`, `seed`, help strings), `.gitignore` (`.env.local`), `deploy/docker-compose.yml` (api `Auth__Issuer`, `Auth__Audience`), `docs/api/README.md`, `docs/data-dictionary/identity.md`, `docs/data-dictionary/redis-and-kafka.md`, `docs/superpowers/specs/2026-09-16-foundation-design.md`, `docs/test/integration.md` (or `docs/test/README.md`, see Step 4), `docs/dev-setup.md`, `README.md`, `tools/compose_check.py` only if it asserts the api environment keys

**Interfaces:**
- Consumes: every endpoint from Tasks 7–8; `dotnet-ef` local tool and `TenancyDbContextFactory` (Task 2); Makefile variables `COMPOSE`, `ENV_FILE` (`-include`d, so `MSSQL_SA_PASSWORD` is a make variable).
- Produces: `make migrate` (idempotent), `make seed` (idempotent; writes `CRM_API_KEY` to `.env.local`), docs consistent with rulings P1–P9.

- [ ] **Step 1: `tools/seed.py`**

```python
#!/usr/bin/env python3
"""Bootstrap the demo tenant against a running Development API (make seed).

Idempotent: an existing tenant (409) is reused; a new API key is minted only when
.env.local does not already hold one. Standard library only.
"""
import json
import os
import sys
import urllib.error
import urllib.request

BASE = os.environ.get("CRM_API_URL", "http://localhost:8080").rstrip("/")
ENV_LOCAL = os.environ.get("CRM_ENV_LOCAL", ".env.local")
ADMIN_EMAIL, VIEWER_EMAIL, PASSWORD = "admin@demo.local", "viewer@demo.local", "Demo-Passw0rd!"


def call(method, path, body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method)
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raw = error.read()
        return error.code, json.loads(raw) if raw else None


def fail(step, status, body):
    detail = (body or {}).get("detail") or body
    sys.exit(f"seed: {step} failed with HTTP {status}: {detail}")


def existing_key():
    if not os.path.exists(ENV_LOCAL):
        return None
    with open(ENV_LOCAL, encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("CRM_API_KEY=") and line.strip() != "CRM_API_KEY=":
                return line.strip().split("=", 1)[1]
    return None


def main():
    status, body = call("POST", "/v1/tenants", {
        "name": "Demo", "slug": "demo",
        "adminEmail": ADMIN_EMAIL, "adminPassword": PASSWORD,
        "viewerEmail": VIEWER_EMAIL, "viewerPassword": PASSWORD,
    })
    if status == 201:
        print(f"seed: created tenant demo ({body['tenantId']})")
    elif status == 409:
        print("seed: tenant demo already exists")
    elif status == 404:
        sys.exit("seed: POST /v1/tenants is not mapped; the API must run in Development")
    else:
        fail("bootstrap", status, body)

    status, body = call("POST", "/v1/auth/login", {"email": ADMIN_EMAIL, "password": PASSWORD})
    if status != 200:
        fail("login", status, body)
    token = body["accessToken"]
    print(f"seed: login ok as {ADMIN_EMAIL} (expires in {body['expiresIn']} s)")

    if existing_key():
        print(f"seed: {ENV_LOCAL} already holds CRM_API_KEY; not minting another")
        return

    status, body = call("POST", "/v1/api-keys", {"name": "sdk"}, token)
    if status != 201:
        fail("create api key", status, body)
    with open(ENV_LOCAL, "a", encoding="utf-8") as handle:
        handle.write(f"CRM_API_KEY={body['key']}\n")
    print(f"seed: API key sdk ({body['prefix']}…) written to {ENV_LOCAL}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Makefile, gitignore, compose**

`Makefile` — replace the `migrate` and `seed` stubs:

```make
CRM_CONNECTION_STRING ?= Server=localhost,1433;Database=crm;User Id=sa;Password=$(MSSQL_SA_PASSWORD);TrustServerCertificate=True;Encrypt=False

migrate: env ## apply EF Core migrations (module contexts with migrations: Tenancy)
	dotnet tool restore
	ConnectionStrings__Crm="$(CRM_CONNECTION_STRING)" dotnet ef database update \
		--project src/Modules/Tenancy/Tenancy.Infrastructure \
		--startup-project src/Modules/Tenancy/Tenancy.Infrastructure \
		--context TenancyDbContext

seed: ## bootstrap the demo tenant, Admin + Viewer users and an API key (API must run in Development)
	python3 tools/seed.py
```

Profiles and Segmentation have no migrations yet; their plans append a `dotnet ef database update` line each. Update the header comment of the Makefile if it still says these targets "fail loudly".

`.gitignore` — add `.env.local`.

`deploy/docker-compose.yml` — `api.environment` gains (the signing key line already exists):

```yaml
      Auth__Issuer: crm
      Auth__Audience: crm-api
```

Run `make compose-check`; if `tools/compose_check.py` compares the api environment keys exactly, add the two keys there.

- [ ] **Step 3: Exit-gate run against the compose stack**

```bash
make tunnel-apps
make up            # rebuilds api with the Tenancy module; api runs in Development and migrates on start
make migrate       # no-op after the API migrated; proves the target works (expect "No migrations were applied" or "already up to date")
make seed
make seed          # second run: "already exists" / "already holds CRM_API_KEY"
python3 - <<'EOF'
import json, urllib.request
req = urllib.request.Request("http://localhost:8080/v1/auth/login", data=json.dumps({"email": "admin@demo.local", "password": "Demo-Passw0rd!"}).encode(), headers={"Content-Type": "application/json"}, method="POST")
with urllib.request.urlopen(req, timeout=30) as r:
    print(r.status, r.headers.get("Set-Cookie", "")[:40], list(json.load(r).keys()))
EOF
```

Expected: `200 crm_refresh=… ['accessToken', 'expiresIn']`. `make migrate` reaches SQL Server through the tunnel's `localhost:1433`. Record every output line in the report. Do not commit `.env.local`.

- [ ] **Step 4: Documentation (keep `python3 tools/docs_check.py` at 0 errors after each file)**

- `docs/api/README.md` § Tenancy: add the row `| POST | /auth/logout | JWT | Revokes the caller's refresh tokens, clears the cookie, 204 |`; extend the `/tenants` row with "optional `viewerEmail` + `viewerPassword` create a Viewer"; state that `GET /api-keys` returns `{ items, nextCursor }`; add one sentence under Errors: problem bodies also carry `code` (stable machine-readable error code).
- `docs/data-dictionary/redis-and-kafka.md`: the `apikey:{prefix}` row's value becomes ``string → `{tenantId:N}:{keyId:N}:{keyHashHex}` `` with the note "a hit still verifies the full key in constant time".
- `docs/data-dictionary/identity.md`: ApiKeys and RefreshTokens gain rows `TenantId uniqueidentifier no`, `CreatedAt datetime2(3) no`, `UpdatedAt datetime2(3) no` (Kernel `Entity`); add the seeded role ids; note `__EFMigrationsHistory_identity`.
- `docs/superpowers/specs/2026-09-16-foundation-design.md`: amend §6.1 (migration name, ruling P5), §6.2 (P1, P2), §6.4 (P4 probes, P6 viewer fields, list shape), §7.1 (P7 export order; handler contract `IResult` or `Error` + `ErrorResultFilter`). Edit only those passages.
- `docs/test/integration.md`: under IT-032, IT-033 and IT-037 add a line `- Plan B note: exercised through the Testing-only probes (/v1/_test/read-probe, write-probe, ingest-probe) until /v1/track and /v1/segments exist.` If the docs checker rejects an extra bullet in a test case, put the same three notes in `docs/test/README.md` under a new heading `## Interim probes` instead.
- `docs/dev-setup.md`: everyday commands gain `make migrate`, `make seed` (writes `.env.local`), `make openapi`; a short "Logging in" snippet using the seeded admin.
- `README.md`: status table — Tenancy row "Complete"; quickstart gains `make seed`.

- [ ] **Step 5: Full verification and commit**

```bash
dotnet build Crm.sln -warnaserror --nologo
make test            # unit + arch + contract + integration
make docs-check
make compose-check
make e2e             # web shell still green behind the rebuilt api
```

Expected: every suite green, zero skipped; docs check `0 errors`; record all counts.

```bash
git add tools/seed.py Makefile .gitignore deploy/docker-compose.yml tools/compose_check.py docs README.md
git commit -m "feat(tenancy): wire make migrate and make seed, and align docs with the tenancy implementation

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Plan Self-Review

- **Spec coverage:** §6.1 → Task 2 (schema, indexes, role seed, filters; AT-006). §6.2 → Tasks 5–6 (pipeline order, JWT/API-key resolution, Redis cache with 10 min TTL, constant-time compare, LastUsedAt throttle, fail-closed 500, API keys limited to Ingest). §6.3 → Tasks 3 and 7 (HS256 from configuration, 15 min / 7 d, cookie attributes, rotation, reuse detection, policies). §6.4 → Tasks 6–8 (all seven endpoints, `ingest` limiter, Testing-only probe). §6.5 → UT-050…053 Tasks 1 and 3, UT-056 Task 4, AT-006 Task 2, IT-032 Task 5, IT-033/035 Task 6, IT-036 Task 7, IT-034/037/038 Task 8. §7.1 → Tasks 4 and 9 (problem mapper, exception handler with `traceId`, OpenAPI export and committed document, configuration keys). §8.3 → Task 10 (`migrate`, `seed`). §11 exit gate → Task 10 Step 3. Serilog `TenantId`/`UserId` enrichment → logging scope in `TenantResolutionMiddleware` (Task 5; Serilog already enriches from the log context).
- **Deliberately out of scope:** `docker-compose.observability.yml`, Grafana, Dependabot, LT-005 (plan C); RS256 keys; account lockout and email confirmation flows; user-management endpoints beyond the bootstrap's optional Viewer; Worker registration of the Tenancy module (the Worker authenticates nothing).
- **Type consistency check:** `TenancyClaims.*` (Task 3) are the only claim names used in Tasks 5–8 and in `TenancySeed`. `AuthSchemes/AuthPolicies/RateLimitPolicies/EndpointTags` (Task 5) are used verbatim in Tasks 6–8. `IApiKeyAuthenticator.EvictAsync(string)` (Task 6) is what `ApiKeyEndpoints.RevokeAsync` calls (Task 8). `TenancyErrors` members used in Task 8 are all defined in Task 7. `CrmApiFactory.CreateApiClient()` and `TenancySeed` members (Task 5) match their uses in Tasks 6–8. `RefreshToken.HashOf` / `ApiKey.Hash` names differ on purpose and are used consistently.
- **Known adjust-points (implementer resolves against the compiled API and reports):** `GetDeclaredQueryFilters()` return type (Task 2); `Microsoft.OpenApi` 2.x member shapes (Task 9); `dotnet-ef` exact version (Task 2); CA2100 on test SQL (Task 2).

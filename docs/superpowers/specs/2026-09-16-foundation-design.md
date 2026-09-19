# CRM Platform — Foundation Design: Solution Skeleton, Runtime Infrastructure, Tenancy

- **Date:** 2026-09-16
- **Status:** Approved for planning
- **Parent spec:** [2026-09-15-crm-slice1-design.md](2026-09-15-crm-slice1-design.md) (authority for everything not restated here)
- **Test cases:** [docs/test](../../test/README.md) — this foundation must turn the cases in §7 green
- **Scope:** everything needed before feature modules can be implemented: the .NET solution and build hygiene, `Crm.Kernel`, `Crm.Contracts`, the full Tenancy module, both hosts with health and observability wiring, the React shell, docker-compose runtime, Makefile, CI, and developer setup notes.

---

## 1. Goals and non-goals

Goals:

1. `git clone && make up` yields a healthy stack (SQL Server, Kafka, Redis, API, Worker, Web) on the developer's Podman machine and on Docker Desktop.
2. A developer can bootstrap a tenant, log in, mint an API key, and call an authenticated endpoint end to end.
3. Module boundaries, tenant scoping, contract snapshots, and the OpenAPI document are enforced by tests from the first commit.
4. Every later module plan starts from a compiling solution with DI slots, a migration pipeline, and passing CI.

Non-goals (later plans): ingestion endpoints, Kafka consumers, profile and segment tables, real-time evaluation, dashboard features, load tests.

---

## 2. Local environment (as found on 2026-09-16)

| Tool | State | Consequence |
|---|---|---|
| .NET SDK | 10.0.400 via mise | `global.json` pins `10.0.400` with `rollForward: latestPatch` |
| Container engine | Podman 6.1.1 machine (libkrun, 5 CPU, 8 GB) exposed through `docker` CLI context `podman` | Compose runs via standalone `docker-compose` v5.5.1; Testcontainers needs `DOCKER_HOST` pointing at the podman socket and `TESTCONTAINERS_RYUK_DISABLED=true` |
| amd64 emulation | `qemu-x86_64` only; Podman 6.1.1 with the libkrun provider has no Rosetta | Simple amd64 binaries run; SQL Server 2022 (SQLPAL) segfaults (verified 2026-09-16). Locally the arm64-native `mcr.microsoft.com/azure-sql-edge:latest` is used via `MSSQL_IMAGE` in `deploy/.env`; CI and amd64 hosts use `mssql/server:2022-latest`. Edge is retired (no updates) and lacks a few enterprise features; slice 1 uses none of them |
| Node | 26.8, npm 11 | Vite/React toolchain native |
| k6 | not installed | `brew install k6` documented; load tests are a later plan |
| GitHub CLI | 2.100 | CI on GitHub Actions |

`docs/dev-setup.md` records these facts and the exact environment variables. CI runs on `ubuntu-latest` (amd64) where SQL Server runs natively.

---

## 3. Solution layout and build hygiene

Layout is spec §2.3 verbatim. Additions at the repository root:

| File | Purpose |
|---|---|
| `global.json` | SDK 10.0.400, `rollForward: latestPatch` |
| `Directory.Build.props` | `TargetFramework net10.0`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`, `EnforceCodeStyleInBuild true`, `AnalysisLevel latest-recommended`, deterministic builds |
| `Directory.Packages.props` | central package management; every package version lives here only |
| `.editorconfig` | C# style (file-scoped namespaces, `var` when apparent, private fields `_camelCase`), 4-space indent, LF |
| `Crm.sln` | solution folders `Hosts`, `Modules/<Module>`, `Shared`, `Tests` |
| `Makefile` | `up`, `down`, `logs`, `seed`, `test`, `test-unit`, `test-integration`, `docs-check`, `openapi`, `web-dev` |

Project conventions:

- Each module exposes exactly one public DI entry point `IServiceCollection Add<Module>Module(this IServiceCollection, IConfiguration)` in its Infrastructure project. Hosts reference Infrastructure projects only (AT-005).
- Each module with persistence has one `DbContext` named `<Module>DbContext` on schema `<module>` with `MigrationsHistoryTable("__EFMigrationsHistory_<module>")` and migrations in `<Module>.Infrastructure/Migrations`.
- Test projects: `<Module>.UnitTests` (xUnit, FluentAssertions, NSubstitute), `Crm.IntegrationTests` (xUnit, Testcontainers, `WebApplicationFactory`), `Crm.ArchTests` (NetArchTest.Rules), `Crm.Contracts.UnitTests`, `Crm.Kernel.UnitTests`, `Crm.Api.UnitTests`. Trait `Category=Unit|Integration|Architecture|Contract` on every class so CI filters work.
- Empty modules in this phase (Ingestion, Profiles, Segmentation) still ship their projects, the `Add<Module>Module` method, and for Profiles and Segmentation an empty `DbContext` on their schema. This makes AT-001…AT-006 and the migration pipeline real before any feature lands.

---

## 4. Crm.Kernel

Namespace `Crm.Kernel`. No package dependencies except `Microsoft.EntityFrameworkCore` (for the interceptor and model-builder helper) and `StackExchange.Redis` abstractions are **not** referenced; key building is string-only.

| Type | Contract |
|---|---|
| `Entity` | `Guid Id` (UUIDv7 via `Guid.CreateVersion7()`), `Guid TenantId`, `DateTime CreatedAt`, `DateTime UpdatedAt` (UTC) |
| `Error` | `record Error(ErrorKind Kind, string Code, string Message, IReadOnlyDictionary<string, string[]>? Fields = null)`; `ErrorKind` ∈ `Validation, NotFound, Conflict, Forbidden, Unauthorized, Unexpected` |
| `Result` / `Result<T>` | `IsSuccess`, `Error`, `Value`; factories `Ok`, `Fail(Error)`; `Match` |
| `ITenantContext` | `Guid TenantId { get; }` (throws `TenantContextMissingException` when unresolved), `bool IsResolved`, `Guid? UserId`, `TenantPrincipalKind Kind` ∈ `User, ApiKey, None` |
| `TenantContextMissingException` | thrown by any data access when `IsResolved` is false |
| `TenantMismatchException` | thrown by the save interceptor when an entity's `TenantId` differs from the context |
| `IClock` | `DateTime UtcNow`; `SystemClock` implementation |
| `TenantSaveChangesInterceptor` | on `SavingChanges`: stamps `TenantId`, `CreatedAt`, `UpdatedAt` on added entities; verifies `TenantId` on modified/deleted; refreshes `UpdatedAt` |
| `ITenantScopedDbContext` | `Guid CurrentTenantId { get; }`, implemented by every module `DbContext` |
| `ModelBuilderExtensions.ApplyTenantFilters<TContext>(this ModelBuilder, TContext context)` where `TContext : DbContext, ITenantScopedDbContext` | adds `e => e.TenantId == context.CurrentTenantId` to every `Entity`-derived type in the model. The filter closes over the `DbContext` instance rather than a resolved tenant value because EF Core caches the compiled model per context *type*: a filter capturing a resolved `Guid` would freeze the first tenant it saw for every later query against that context type. |
| `RedisKeys` | static builders: `IdentityCache(tenant, kind, value)`, `SegmentMembers(tenant, segment)`, `EventCounter(tenant, segment, eventName, window, profile)`, `PreviewCount(tenant, hashHex)`, `ApiKey(prefix)`, `SegmentsChangedChannel` — shapes exactly as in the [data dictionary](../../data-dictionary/redis-and-kafka.md) |
| `IIdGenerator` / `UuidV7IdGenerator` | `Guid NewId()` wrapping `Guid.CreateVersion7()` (mockable) |

Tests: UT-054, UT-055, UT-057 in `Crm.Kernel.UnitTests`. The interceptor test uses the EF in-memory provider with a `TestEntity`.

---

## 5. Crm.Contracts

Namespace `Crm.Contracts`. Depends on nothing but the BCL.

- Records with camelCase JSON (System.Text.Json): `TrackEventRaw`, `IdentifyRaw`, `EventTracked`, `ProfileUpdated`, `ProfileMerged` with the exact fields of the [data dictionary message table](../../data-dictionary/redis-and-kafka.md). `ProfileSnapshot` record shared by `EventTracked` and `ProfileUpdated`.
- `KafkaTopics`: `EventsRaw = "events.raw"`, `EventsProcessed = "events.processed"`, `EventsDlq = "events.dlq"`; `Partitions = 12`; retention constants in milliseconds.
- `KafkaHeaders`: `TenantId = "tenant-id"`, `MessageType = "message-type"`, `ContentType = "content-type"`, `TraceParent = "traceparent"`, DLQ headers `error`, `stack`, `attempt`, `source-topic`, `source-partition`, `source-offset`.
- `ContractJson.Options`: `JsonSerializerOptions` with camelCase, `DefaultIgnoreCondition = WhenWritingNull`, `PropertyNameCaseInsensitive = true`.

Tests: CT-004 snapshot tests in `Crm.Contracts.UnitTests` against `tests/fixtures/contracts/<Type>.json`.

---

## 6. Tenancy module

### 6.1 Persistence (schema `identity`)

Exactly the [identity data dictionary](../../data-dictionary/identity.md): ASP.NET Core Identity tables via `IdentityDbContext<AppUser, IdentityRole, string>` with `AppUser { Guid TenantId; string? DisplayName }`, plus `Tenant`, `ApiKey`, `RefreshToken` entities. `Tenant` is the one aggregate not derived from `Entity` (it has no `TenantId`). Identity tables are excluded from `ApplyTenantFilters`; `AppUser`, `ApiKey`, `RefreshToken` are filtered.

First migration class `InitialIdentity` (file `<timestamp>_InitialIdentity.cs`; C# identifiers cannot start with a digit, so the spec's earlier `0001_` label is descriptive only — ruling P5) creates the schema, tables, indexes (`UX_Tenants_Slug`, `UX_ApiKeys_Prefix`, `IX_ApiKeys_TenantId`, `IX_AspNetUsers_TenantId`, `UX_RefreshTokens_TokenHash`, `IX_RefreshTokens_UserId`) and seeds roles `Admin`, `Viewer`. A second, additive migration, `EnforceUniqueEmail` (ruling P13), replaces Identity's non-unique `EmailIndex` with a unique, filtered index on `NormalizedEmail`, since login looks a user up by normalized email alone across all tenants.

### 6.2 Tenant resolution middleware

Order in the pipeline: exception handler → HTTPS/forwarded headers → authentication → **tenant resolution** → rate limiter → authorization → endpoints.

API-key lookup and verification happen inside an authentication handler (the `ApiKey` scheme), not in `TenantResolutionMiddleware`: authorization policies need an authenticated principal before the middleware runs, and this keeps the observable behaviour below unchanged (ruling P1).

1. If the request carries a validated JWT: `TenantId = tenant_id claim`, `UserId = sub`, `Kind = User`. A JWT without `tenant_id` leaves the context unresolved.
2. Else if `X-Api-Key` is present: parse `crm_live_<32>`; look up `apikey:{prefix}` in Redis; on miss load `ApiKeys` by `Prefix`, compare SHA-256 in constant time, cache `"{tenantId:N}:{keyId:N}:{keyHashHex}"` for 10 min (not the bare tenant id — a cache hit still verifies the full key in constant time, otherwise a key with a changed suffix would authenticate; ruling P2); reject when revoked or mismatched → 401. `Kind = ApiKey`. Update `LastUsedAt` at most once per minute (in-memory last-write timestamp per prefix).
3. Else unresolved. Endpoints marked `AllowAnonymous` (login, refresh, tenants bootstrap, health) proceed; everything else returns 401.
4. Any data access on an unresolved context throws `TenantContextMissingException` → 500 with `traceId`, logged at Error (IT-032).

API-key principals may call only endpoints tagged `Ingest`; calling anything else returns 403 (IT-033).

Cache-read resilience (ruling P11, found in review of Task 6): an unparseable cache entry (`ApiKeyCacheEntry.TryParse` failure, e.g. a corrupt or foreign value) is treated as a cache miss and falls through to SQL, not as an error. `RedisTimeoutException` is caught alongside `RedisException` on cache read and write, since in StackExchange.Redis 3.x it derives from `TimeoutException` and would otherwise answer 500 on every ingest request instead of falling back to SQL. `LastUsedAt` writes are best-effort (failures are swallowed). Cache *eviction* on revoke is not swallowed: a failure there surfaces, because a stale cache entry after revoke would let a revoked key keep authenticating.

### 6.3 Authentication and tokens

- JWT bearer, HS256 in Development with `Auth:SigningKey` from configuration (compose supplies a fixed dev key); the validation parameters are configuration-driven so RS256 can replace it by configuration plus the algorithm allow-list. `ValidAlgorithms` is pinned to `HS256`, so an HS384/HS512 token signed with the same key does not validate. Access token 15 min; claims `sub`, `tenant_id`, `role`, `jti`.
- Refresh token: 32 random bytes, base64url, stored as SHA-256 in `RefreshTokens`, expiry 7 d, delivered as cookie `crm_refresh` with `HttpOnly; Secure; SameSite=Strict; Path=/v1/auth`. `Secure` is relaxed to `false` only when `ASPNETCORE_ENVIRONMENT=Development` and the request is plain HTTP on localhost. Refresh rotates: old token gets `RevokedAt` and `ReplacedByTokenId`; presenting a revoked token returns 401 and revokes the whole chain for that user (reuse detection).
- Roles as JWT `role` claim; authorization policies `AdminOnly`, `AdminOrViewer`, `IngestOnly`.
- Ruling P12 (found in review of Task 7 — the block above predates it; the committed code is authoritative): `AuthService.RefreshAsync` also checks that the tenant is `Active` — a suspended tenant could otherwise keep minting access tokens for up to 7 d from a token issued before suspension — and rotation claims the old token with one conditional update (`WHERE RevokedAt IS NULL`) in the same transaction as the insert, so two concurrent refreshes of one cookie cannot fork the chain. A caller that loses that claim gets 401. Reuse detection has no grace window: a token already revoked when read revokes the whole chain, and this includes a late duplicate of an honest double-submit, which is indistinguishable from a replay — the guarantee is that one refresh token never yields two descendants, so web clients must single-flight their refresh call. Unknown emails verify a dummy hash so login timing does not reveal which accounts exist. `Tenant` gains `Suspend()` / `Activate()`.

### 6.4 Endpoints (all under `/v1`, problem+json errors)

| Endpoint | Auth | Behaviour |
|---|---|---|
| `POST /auth/login` | anonymous | `{ email, password }` → `200 { accessToken, expiresIn: 900 }` + `crm_refresh` cookie; 401 on bad credentials (same message for unknown email and wrong password) |
| `POST /auth/refresh` | anonymous, cookie | rotates; `200 { accessToken, expiresIn }` + new cookie; 401 on missing/expired/revoked |
| `POST /auth/logout` | JWT | revokes the current refresh chain, clears cookie, 204 |
| `POST /tenants` | anonymous, **Development only** | `{ name, slug, adminEmail, adminPassword }` → `201 { tenantId, slug }`; creates tenant + Admin user; optional `viewerEmail` + `viewerPassword` also create a Viewer (ruling P6 — the spec defines no separate user-management endpoint, and `make seed` needs a Viewer); 404 in any other environment (endpoint not mapped) |
| `GET /api-keys` | Admin | `200 { items, nextCursor }`; each item `{ id, name, prefix, createdAt, lastUsedAt, revokedAt }` |
| `POST /api-keys` | Admin | `{ name }` → `201 { id, name, prefix, key }`; `key` never retrievable again |
| `DELETE /api-keys/{id}` | Admin | sets `RevokedAt`, deletes `apikey:{prefix}` from Redis, 204 |

Rate limiting: ASP.NET `RateLimiter` with a token-bucket policy `ingest` partitioned by API-key prefix (2,000 tokens, replenish 2,000 per minute); 429 responses carry `Retry-After`. The policy is applied to endpoints tagged `Ingest`; in this phase no such endpoint exists yet, so IT-035 targets a test-only `POST /v1/_test/ingest-probe` endpoint mapped only in the `Testing` environment and removed when the real `/track` lands. Inside a partitioned limiter .NET disables `AutoReplenishment` and instead runs a ~100 ms heartbeat calling `TryReplenish()`, so the bucket refills continuously at 2,000 per minute (≈33 tokens/s) rather than all at once at the top of the minute (ruling P10, found in execution of Task 6); IT-035 is sized accordingly.

Ruling P13 (found in the whole-branch review): a second token-bucket policy, `auth` (20 tokens, replenish 20 per minute, partitioned by client address), is applied to `POST /auth/login`, `POST /auth/refresh` and `POST /tenants` — anonymous, unlimited endpoints that were otherwise the cheapest request to send and, because of the login timing mitigation, the most expensive to serve. Behind the bundled nginx proxy the partition's "client address" depends on `ForwardedHeadersOptions` trusting the proxy hop: `ForwardedHeaders:KnownNetworks` (default empty, i.e. loopback only) lists the networks a forwarded `X-Forwarded-For`/`X-Forwarded-Proto` is honoured from, and the compose deployment sets it to the private compose network only. A real deployment sets `ForwardedHeaders:KnownNetworks` to the reverse proxy's own address (not the broad private ranges compose uses), because any peer inside a trusted network can supply `X-Forwarded-For` and so choose its rate-limit partition.

Ruling P4 (test-only probes standing in for endpoints from later plans): cases that name endpoints this plan does not implement yet are exercised through probes mapped only in the `Testing` environment and excluded from OpenAPI — `POST /v1/_test/ingest-probe` (Ingest, 202) stood in for `POST /v1/track`; `GET /v1/_test/read-probe` (AdminOrViewer, reads tenant data) for `GET /v1/segments`; `POST /v1/_test/write-probe` (AdminOnly) for `POST /v1/segments` and `/run`. Plan D removed `ingest-probe` now that `/v1/track` exists (ruling D12); `read-probe` and `write-probe` remain until the Segmentation plan repoints those tests and deletes them.

### 6.5 Tests

UT-050…UT-053 (`Tenancy.UnitTests`), UT-054, UT-055, UT-057 (`Crm.Kernel.UnitTests`), UT-056 (`Crm.Api.UnitTests`), AT-006, IT-032…IT-038, IT-041.

---

## 7. Hosts

### 7.1 Crm.Api

- Minimal APIs grouped per module (`MapTenancyEndpoints(RouteGroupBuilder)` etc.), versioned under `/v1`.
- OpenAPI via `Microsoft.AspNetCore.OpenApi` (`AddOpenApi`), Scalar UI served at `/swagger` in Development. CLI mode `dotnet run --project src/Hosts/Crm.Api -- --export-openapi <path>` builds the app, writes the document, exits 0 (CT-001). `docs/api/openapi.json` is committed from this export. The Development-only tenant bootstrap endpoint is mapped whenever `IsDevelopment() || exportPath is not null` (ruling P7), and export maps every endpoint group before writing the document, so the committed file does not depend on which environment CI happens to export in. The export briefly starts the host on an OS-assigned loopback port — ASP.NET Core only finalizes the endpoint metadata the OpenAPI generator reads once routing starts — and needs no database, Redis, or Kafka connection. `dotnet run --project <dir>` resolves a relative `--export-openapi` path against the *project* directory, not the caller's working directory, so `make openapi` and CI both pass an absolute path.
- Problem details: `IProblemDetailsService` + custom mapper `ResultProblemMapper` (`Error` → status/type/`errors`) and a global exception handler adding `traceId`; `type` is `https://crm.example/problems/<kind-lowercase>`. Endpoint handlers return either an `IResult` or a Kernel `Error`; the `ErrorResultFilter` endpoint filter on the `/v1` route group turns an `Error` into the problem+json response.
- Serilog (console JSON sink, enrichers `TenantId`, `TraceId`, `UserId`), OpenTelemetry traces + metrics (ASP.NET Core, HttpClient, EF Core, runtime) with OTLP exporter configured by `OTEL_EXPORTER_OTLP_ENDPOINT` (no-op when unset), Prometheus scraping endpoint `/metrics` via `OpenTelemetry.Exporter.Prometheus.AspNetCore`.
- Health: `/health/live` (self), `/health/ready` (SQL Server `SELECT 1`, Redis `PING`, Kafka admin `GetMetadata` with 3 s timeout). Both outside `/v1`, anonymous.
- Startup in Development: `MigrateAsync()` for every module `DbContext`. Other environments: migrations are applied by `make migrate` (`dotnet ef database update` per context) — never at startup.
- Configuration keys (environment variables in compose): `ConnectionStrings__Crm`, `Redis__Configuration`, `Kafka__BootstrapServers`, `Auth__SigningKey`, `Auth__Issuer`, `Auth__Audience`, `OTEL_EXPORTER_OTLP_ENDPOINT`.

### 7.2 Crm.Worker

Generic host with `AddHealthChecks` exposed on port 8081 (`/health/live`, `/health/ready` with the same three checks), Serilog and OpenTelemetry wiring identical to the API, module registrations, and a single `StartupProbeService` that logs Kafka metadata once. Consumers arrive with the Profiles plan.

### 7.3 Crm.Web

Vite 6 + React 19 + TypeScript strict; TanStack Router (file-based) and TanStack Query; Tailwind + shadcn/ui initialized (button, input, card); Zod; ESLint + Prettier; Vitest + Testing Library; Playwright configured against `http://localhost:3000`. Routes: `/login` placeholder form (no auth call yet) and `/` showing `/health/ready` status. `Dockerfile` builds with Node 22 LTS image (Node 26 locally is fine; the image pins the LTS) and serves via nginx with `location /v1/ { proxy_pass http://api:8080; }` and `/health/` proxied likewise. Published port 3000.

---

## 8. Runtime infrastructure (`deploy/`)

### 8.1 docker-compose.yml

| Service | Image | Notes |
|---|---|---|
| `sqlserver` | `${MSSQL_IMAGE}` — default `mcr.microsoft.com/mssql/server:2022-latest`; `deploy/.env` sets `mcr.microsoft.com/azure-sql-edge:latest` on arm64 hosts without Rosetta | `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD` from `.env`, `MSSQL_PID=Developer`, `MSSQL_MEMORY_LIMIT_MB=2048`, volume `sqldata`; healthcheck is TCP (`bash -c '</dev/tcp/127.0.0.1/1433'`, 5 s × 36) because Edge ships no `sqlcmd`. No init sidecar: the first EF Core migration creates database `crm` and sets `READ_COMMITTED_SNAPSHOT ON` |
| `kafka` | `apache/kafka:3.9.1` (multi-arch) | KRaft single node, `KAFKA_NODE_ID=1`, controller+broker, listeners `PLAINTEXT://kafka:9092` internal and `EXTERNAL://localhost:19092` for host tools, `KAFKA_AUTO_CREATE_TOPICS_ENABLE=false`; healthcheck `kafka-broker-api-versions.sh` |
| `kafka-init` | same image, one-shot | creates `events.raw`, `events.processed` (12 partitions, `retention.ms=604800000`) and `events.dlq` (12, `retention.ms=2592000000`) idempotently |
| `redis` | `redis:7-alpine` | `--appendonly no`, healthcheck `redis-cli ping` |
| `api` | built from `src/Hosts/Crm.Api/Dockerfile` | port 8080, depends on sqlserver healthy, kafka-init completed, redis healthy; `ASPNETCORE_ENVIRONMENT=Development` |
| `worker` | built from `src/Hosts/Crm.Worker/Dockerfile` | port 8081 (health), same dependencies |
| `web` | built from `src/Hosts/Crm.Web/Dockerfile` | port 3000, depends on api |

`.env.example` holds `MSSQL_SA_PASSWORD`, `AUTH_SIGNING_KEY` (dev only), copied to `.env` by `make up` when absent. Compose project name `crm`.

### 8.2 docker-compose.observability.yml

Second compose file, profile `observability`, started together with the base file by `make up-observability`: `prometheus` (scrapes `api:8080/metrics`, `worker:8081/metrics`), `grafana` (provisioned Prometheus datasource, one dashboard `crm-overview` with HTTP rate/latency plus ingest rate, consumer lag, DLQ, segment-run and real-time-evaluation panels — the `crm_*` panels show "no data" until later plans), `jaeger` (v2 image, OTLP gRPC receiver on 4317, UI on 16686). The file overrides `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317` on `api` and `worker`.

Rulings (plan C): configuration is baked into images built from `deploy/observability/{prometheus,grafana}` — no bind mounts, because with a remote engine a bind path resolves on the engine host (C1). Host ports are `${PROMETHEUS_HOST_PORT:-9090}`, `${GRAFANA_HOST_PORT:-3001}` (web owns 3000), `${JAEGER_HOST_PORT:-16686}`; 4317 is not published (C2). `make tunnel-observability` forwards them from a remote engine. `make down` and `make ps` always include this file so its containers are never orphaned (C7). Grafana allows anonymous Viewer access; because this project's engine is commonly a remote host reached over ssh rather than the workstation itself, the three published ports bind to the engine's loopback interface only (`127.0.0.1:<port>:<container port>`) so anonymous Grafana and unauthenticated Prometheus/Jaeger are not reachable from the LAN — the ssh tunnel still reaches them because its forward resolves `localhost` on the engine (C9).

### 8.3 Makefile targets

| Target | Does |
|---|---|
| `up` | copy `.env.example` → `.env` if missing; `docker-compose -f deploy/docker-compose.yml up -d --build`; wait for `api` healthy; print URLs |
| `up-observability` | base + observability files with `--profile observability`, `up -d --build --wait` |
| `down` | `down -v` (removes volumes; explicit flag `KEEP=1` keeps data) |
| `logs` | follow logs of api and worker (`SERVICES="…"` selects others) |
| `seed` | `POST /v1/tenants` for `demo` with `admin@demo.local` / `Demo-Passw0rd!`, create Viewer user, mint API key `sdk`, write plaintext to `.env.local`; profile/event seeding is appended by the Profiles plan |
| `migrate` | `dotnet ef database update` for each module context against `.env` connection string |
| `test-unit` | `dotnet test --filter "Category!=Integration"` |
| `test-integration` | exports `DOCKER_HOST` from `docker context inspect` and `TESTCONTAINERS_RYUK_DISABLED=true`, then `dotnet test --filter Category=Integration` |
| `test` | both |
| `docs-check` | `python3 tools/docs_check.py` |
| `compose-check` | renders both compose configurations and runs `tools/compose_check.py` (CT-005), the second with `--observability` |
| `openapi` | export to `docs/api/openapi.json` |
| `web-dev` | `npm run dev` in Crm.Web with proxy to `localhost:8080` |

---

## 9. Continuous integration

`.github/workflows/ci.yml`, trigger: pull requests and pushes to `main`. Jobs on `ubuntu-latest`:

1. **dotnet** — setup .NET 10.0.400, restore (cached), build with warnings as errors, `dotnet test --filter "Category!=Integration"` with TRX upload.
2. **integration** — needs docker (default on the runner); `dotnet test --filter Category=Integration`; 25 min timeout.
3. **contracts** — `python3 tools/docs_check.py`; OpenAPI export and `diff -u docs/api/openapi.json build/openapi.json` (the exported document against the committed one); `docker compose -f deploy/docker-compose.yml config -q` and the observability file; `python3 -m unittest discover -s tools/tests` (compose_check, observability assets, Testcontainers image pins).
4. **web** — Node 22, `npm ci`, `npm run lint`, `npm run typecheck`, `npm test -- --run`.

Branch protection is out of scope; the workflow badge goes in the README.

---

## 10. Test cases in scope

| Case ids | Where they become green |
|---|---|
| UT-050, UT-051, UT-052, UT-053 | plan B (Tenancy) |
| UT-054, UT-055, UT-057 | plan A (Kernel) |
| UT-056 | plan B (API problem mapper) |
| AT-001, AT-002, AT-003, AT-004, AT-005 | plan A |
| AT-006 | plan B (first tenant-scoped entities exist) |
| CT-001 | plan B (first real endpoints in the document) |
| CT-003, CT-004, CT-005 | plan A |
| IT-032, IT-033, IT-034, IT-035, IT-036, IT-037, IT-038 | plan B |
| IT-041 | plan A |

All other cases remain pending and are listed as such in each plan's header so traceability stays honest.

---

## 11. Plan split

| Plan | Deliverable | Exit gate |
|---|---|---|
| **A — skeleton and infrastructure** | root build files, all projects, Kernel, Contracts, hosts with health/observability, compose, Makefile, CI, web shell, `docs/dev-setup.md` | `make up` healthy on Podman; `dotnet test` green (UT-054/055/057, AT-001…005, CT-004); `docs_check` green; compose config valid; CI green on a pushed commit |
| **B — Tenancy** | identity schema and migration, middleware, JWT + refresh cookie, API keys, endpoints, rate limiter, problem mapper, OpenAPI export and committed document | UT-050…053, UT-056, AT-006, CT-001, IT-032…038 green; `make seed` bootstraps and login works via curl |
| **C — developer experience** | observability profile wired, Grafana dashboard, README status/quickstart refresh, `docs/dev-setup.md` final, LT-005 manual walkthrough recorded | cold-start walkthrough within the LT-005 budgets at foundation scope on the home-lab Podman engine (`tests/Crm.LoadTests/coldstart.sh`, image layers cached); full LT-005 needs `/v1/track` and the seed volume from later plans |

Each plan is executed with subagent-driven development, ends green, and is pushed before the next starts.

---

## 12. Decisions recorded here (candidates for ADRs when they stabilize)

| Decision | Rationale |
|---|---|
| HS256 with configured key in Development; RS256-ready validation parameters | zero infra now, drop-in swap later |
| Migrations applied at API startup only in Development; `make migrate` elsewhere | fast local loop without hiding schema changes in production paths |
| `Microsoft.AspNetCore.OpenApi` + Scalar UI at `/swagger` | built into .NET 10, no Swashbuckle dependency; path kept for the spec's Swagger promise |
| `apache/kafka` KRaft image | official multi-arch image, native on arm64, no ZooKeeper |
| Azure SQL Edge locally, SQL Server 2022 in CI | 2022 segfaults under qemu on the libkrun Podman machine (no Rosetta); Edge is arm64-native and T-SQL/JSON compatible for slice 1; image selected by `MSSQL_IMAGE` so amd64 hosts stay on 2022 |
| Database created by migrations, not a SQL init sidecar | Edge has no `sqlcmd`; EF Core `Migrate()` issues `CREATE DATABASE` itself and the first migration sets `READ_COMMITTED_SNAPSHOT` with `suppressTransaction` |
| No .NET Aspire | compose is the documented contract; Aspire would duplicate it |
| Test-only ingest probe endpoint for IT-035 | rate-limit policy is testable before `/track` exists; removed by plan D (ruling D12) now that `/v1/track` exists |

---

## 13. Risks

| Risk | Mitigation |
|---|---|
| Behaviour differences between Azure SQL Edge (local) and SQL Server 2022 (CI) | slice 1 uses only core T-SQL, JSON functions, computed columns, partitioning; CI is the source of truth; any Edge-only failure is a local-dev note, any CI-only failure blocks |
| Host ports already taken by other local projects (6379 seen) | Redis published on `${REDIS_HOST_PORT:-6380}`; other ports configurable the same way when needed |
| Testcontainers + Podman quirks (Ryuk, socket path) | `make test-integration` sets the variables; documented in `docs/dev-setup.md`; fallback `TESTCONTAINERS_HOST_OVERRIDE` |
| 8 GB VM with SQL Server + Kafka + three .NET processes | SQL Server capped at 2 GB (`MSSQL_MEMORY_LIMIT_MB=2048`), Kafka heap 512 MB, observability profile opt-in |
| Secure cookie on plain-HTTP localhost | Development relaxes `Secure` only for localhost; documented |
| Package versions drift across projects | central package management; weekly grouped Dependabot updates (`.github/dependabot.yml`) |

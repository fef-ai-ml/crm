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
| `ModelBuilderExtensions.ApplyTenantFilters(ITenantContext)` | adds `e => e.TenantId == ctx.TenantId` to every `Entity`-derived type in the model |
| `RedisKeys` | static builders: `IdentityCache(tenant, kind, value)`, `SegmentMembers(tenant, segment)`, `EventCounter(tenant, segment, eventName, window, profile)`, `PreviewCount(tenant, hashHex)`, `ApiKey(prefix)`, `SegmentsChangedChannel` — shapes exactly as in the [data dictionary](../../data-dictionary/redis-and-kafka.md) |
| `IdGenerator` | `Guid NewId()` wrapping `Guid.CreateVersion7()` (mockable) |

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

First migration `0001_InitialIdentity` creates the schema, tables, indexes (`UX_Tenants_Slug`, `UX_ApiKeys_Prefix`, `IX_ApiKeys_TenantId`, `IX_AspNetUsers_TenantId`, `UX_RefreshTokens_TokenHash`, `IX_RefreshTokens_UserId`) and seeds roles `Admin`, `Viewer`.

### 6.2 Tenant resolution middleware

Order in the pipeline: exception handler → HTTPS/forwarded headers → authentication → **tenant resolution** → rate limiter → authorization → endpoints.

1. If the request carries a validated JWT: `TenantId = tenant_id claim`, `UserId = sub`, `Kind = User`. A JWT without `tenant_id` leaves the context unresolved.
2. Else if `X-Api-Key` is present: parse `crm_live_<32>`; look up `apikey:{prefix}` in Redis; on miss load `ApiKeys` by `Prefix`, compare SHA-256 in constant time, cache `TenantId` for 10 min; reject when revoked or mismatched → 401. `Kind = ApiKey`. Update `LastUsedAt` at most once per minute (in-memory last-write timestamp per prefix).
3. Else unresolved. Endpoints marked `AllowAnonymous` (login, refresh, tenants bootstrap, health) proceed; everything else returns 401.
4. Any data access on an unresolved context throws `TenantContextMissingException` → 500 with `traceId`, logged at Error (IT-032).

API-key principals may call only endpoints tagged `Ingest`; calling anything else returns 403 (IT-033).

### 6.3 Authentication and tokens

- JWT bearer, HS256 in Development with `Auth:SigningKey` from configuration (compose supplies a fixed dev key); the validation parameters are configuration-driven so RS256 can replace it without code changes. Access token 15 min; claims `sub`, `tenant_id`, `role`, `jti`.
- Refresh token: 32 random bytes, base64url, stored as SHA-256 in `RefreshTokens`, expiry 7 d, delivered as cookie `crm_refresh` with `HttpOnly; Secure; SameSite=Strict; Path=/v1/auth`. `Secure` is relaxed to `false` only when `ASPNETCORE_ENVIRONMENT=Development` and the request is plain HTTP on localhost. Refresh rotates: old token gets `RevokedAt` and `ReplacedByTokenId`; presenting a revoked token returns 401 and revokes the whole chain for that user (reuse detection).
- Roles as JWT `role` claim; authorization policies `AdminOnly`, `AdminOrViewer`, `IngestOnly`.

### 6.4 Endpoints (all under `/v1`, problem+json errors)

| Endpoint | Auth | Behaviour |
|---|---|---|
| `POST /auth/login` | anonymous | `{ email, password }` → `200 { accessToken, expiresIn: 900 }` + `crm_refresh` cookie; 401 on bad credentials (same message for unknown email and wrong password) |
| `POST /auth/refresh` | anonymous, cookie | rotates; `200 { accessToken, expiresIn }` + new cookie; 401 on missing/expired/revoked |
| `POST /auth/logout` | JWT | revokes the current refresh chain, clears cookie, 204 |
| `POST /tenants` | anonymous, **Development only** | `{ name, slug, adminEmail, adminPassword }` → `201 { tenantId, slug }`; creates tenant + Admin user; 404 in any other environment (endpoint not mapped) |
| `GET /api-keys` | Admin | list `{ id, name, prefix, createdAt, lastUsedAt, revokedAt }` |
| `POST /api-keys` | Admin | `{ name }` → `201 { id, name, prefix, key }`; `key` never retrievable again |
| `DELETE /api-keys/{id}` | Admin | sets `RevokedAt`, deletes `apikey:{prefix}` from Redis, 204 |

Rate limiting: ASP.NET `RateLimiter` with a token-bucket policy `ingest` partitioned by API-key prefix (2,000 tokens, replenish 2,000 per minute); 429 responses carry `Retry-After`. The policy is applied to endpoints tagged `Ingest`; in this phase no such endpoint exists yet, so IT-035 targets a test-only `POST /v1/_test/ingest-probe` endpoint mapped only in the `Testing` environment and removed when the real `/track` lands.

### 6.5 Tests

UT-050…UT-053 (`Tenancy.UnitTests`), UT-054, UT-055, UT-057 (`Crm.Kernel.UnitTests`), UT-056 (`Crm.Api.UnitTests`), AT-006, IT-032…IT-038, IT-041.

---

## 7. Hosts

### 7.1 Crm.Api

- Minimal APIs grouped per module (`MapTenancyEndpoints(RouteGroupBuilder)` etc.), versioned under `/v1`.
- OpenAPI via `Microsoft.AspNetCore.OpenApi` (`AddOpenApi`), Scalar UI served at `/swagger` in Development. CLI mode `dotnet run --project src/Hosts/Crm.Api -- --export-openapi <path>` builds the app, writes the document, exits 0 (CT-001). `docs/api/openapi.json` is committed from this export.
- Problem details: `IProblemDetailsService` + custom mapper `ResultProblemMapper` (`Error` → status/type/`errors`) and a global exception handler adding `traceId`; `type` is `https://crm.example/problems/<kind-lowercase>`.
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

Profile `observability`: `prometheus` (scrapes `api:8080/metrics`, `worker:8081/metrics`), `grafana` (provisioned Prometheus datasource, one dashboard JSON with ingest rate, consumer lag, DLQ, run duration panels — panels show "no data" until later plans), `jaeger` (all-in-one, OTLP receiver). API and Worker get `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317` when the profile is active. `make up-observability` starts both files.

### 8.3 Makefile targets

| Target | Does |
|---|---|
| `up` | copy `.env.example` → `.env` if missing; `docker-compose -f deploy/docker-compose.yml up -d --build`; wait for `api` healthy; print URLs |
| `down` | `down -v` (removes volumes; explicit flag `KEEP=1` keeps data) |
| `logs` | follow logs of api and worker |
| `seed` | `POST /v1/tenants` for `demo` with `admin@demo.local` / `Demo-Passw0rd!`, create Viewer user, mint API key `sdk`, write plaintext to `.env.local`; profile/event seeding is appended by the Profiles plan |
| `migrate` | `dotnet ef database update` for each module context against `.env` connection string |
| `test-unit` | `dotnet test --filter "Category!=Integration"` |
| `test-integration` | exports `DOCKER_HOST` from `docker context inspect` and `TESTCONTAINERS_RYUK_DISABLED=true`, then `dotnet test --filter Category=Integration` |
| `test` | both |
| `docs-check` | `python3 tools/docs_check.py` |
| `openapi` | export to `docs/api/openapi.json` |
| `web-dev` | `npm run dev` in Crm.Web with proxy to `localhost:8080` |

---

## 9. Continuous integration

`.github/workflows/ci.yml`, trigger: pull requests and pushes to `main`. Jobs on `ubuntu-latest`:

1. **dotnet** — setup .NET 10.0.400, restore (cached), build with warnings as errors, `dotnet test --filter "Category!=Integration"` with TRX upload.
2. **integration** — needs docker (default on the runner); `dotnet test --filter Category=Integration`; 20 min timeout.
3. **contracts** — `python3 tools/docs_check.py`; OpenAPI export and `git diff --exit-code docs/api/openapi.json`; `docker compose -f deploy/docker-compose.yml config -q` and the observability file.
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
| **C — developer experience** | observability profile wired, Grafana dashboard, README status/quickstart refresh, `docs/dev-setup.md` final, LT-005 manual walkthrough recorded | fresh-clone walkthrough within the LT-005 budgets on the Podman machine |

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
| Test-only ingest probe endpoint for IT-035 | rate-limit policy is testable before `/track` exists; removed by the Ingestion plan |

---

## 13. Risks

| Risk | Mitigation |
|---|---|
| Behaviour differences between Azure SQL Edge (local) and SQL Server 2022 (CI) | slice 1 uses only core T-SQL, JSON functions, computed columns, partitioning; CI is the source of truth; any Edge-only failure is a local-dev note, any CI-only failure blocks |
| Host ports already taken by other local projects (6379 seen) | Redis published on `${REDIS_HOST_PORT:-6380}`; other ports configurable the same way when needed |
| Testcontainers + Podman quirks (Ryuk, socket path) | `make test-integration` sets the variables; documented in `docs/dev-setup.md`; fallback `TESTCONTAINERS_HOST_OVERRIDE` |
| 8 GB VM with SQL Server + Kafka + three .NET processes | SQL Server capped at 2 GB (`MSSQL_MEMORY_LIMIT_MB=2048`), Kafka heap 512 MB, observability profile opt-in |
| Secure cookie on plain-HTTP localhost | Development relaxes `Secure` only for localhost; documented |
| Package versions drift across projects | central package management; Dependabot config in plan C |

# Foundation Plan A — Solution Skeleton and Runtime Infrastructure

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A compiling, tested .NET 10 modular-monolith solution with `Crm.Kernel`, `Crm.Contracts`, empty module shells, two hosts exposing health and observability, a React shell, compose services for api/worker/web, and CI — so that Plan B (Tenancy) and later module plans start from green.

**Architecture:** One solution, schema-per-module, hosts reference only module Infrastructure projects. Cross-cutting host concerns (Serilog, OpenTelemetry, health checks) live in a new shared project `Crm.Hosting` so `Crm.Api` and `Crm.Worker` do not duplicate them. Architecture tests (NetArchTest) enforce the dependency rules from the first commit. Integration tests run against Testcontainers; the SQL image is chosen by `MSSQL_IMAGE` so the Podman/arm64 machine uses Azure SQL Edge while CI uses SQL Server 2022.

**Tech Stack:** .NET 10.0.400 SDK, C# latest, EF Core 10.0.12, Confluent.Kafka 2.15.1, StackExchange.Redis 3.2.1, Serilog.AspNetCore 10.0.0, OpenTelemetry 1.18.0, Microsoft.AspNetCore.OpenApi 10.0.12 + Scalar.AspNetCore 2.17.3, xUnit 2.9.3, FluentAssertions 7.2.2, NSubstitute 6.2.0, NetArchTest.Rules 1.3.2, Testcontainers 4.15.0, Vite 8.3.0, React 19.3.0, TypeScript 5.9.3, TanStack Router 1.170.36 / Query 5.102.8, Tailwind 4.3.3, Vitest 5.0.1, Playwright 1.63.0, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-16-foundation-design.md` (parent: `docs/superpowers/specs/2026-09-15-crm-slice1-design.md`). Test cases: `docs/test/`.

## Global Constraints

- SDK pinned in `global.json`: `10.0.400`, `rollForward: latestPatch`. Installed runtime is 10.0.11; packages at 10.0.12 are fine (app-local).
- `Directory.Build.props`: `net10.0`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`, `EnforceCodeStyleInBuild true`, `AnalysisLevel latest-recommended`, deterministic builds. Every package version lives only in `Directory.Packages.props` (central package management).
- Project names are exactly the spec's (`Ingestion.Application`, `Profiles.Infrastructure`, `Crm.Kernel`, …); root namespaces are `Crm.<Module>.<Layer>` (e.g. `Crm.Profiles.Infrastructure`), set via `<RootNamespace>`.
- Dependency rules (enforced by `Crm.ArchTests`): modules depend only on `Crm.Kernel` and `Crm.Contracts`; `Ingestion` has no EF/SqlClient/Redis dependency; `Segmentation` never references `Profiles.*`; no module references `Crm.Api`, `Crm.Worker`, or `Crm.Hosting`; hosts reference only `*.Infrastructure` module projects (plus `Crm.Hosting`, `Crm.Kernel`, `Crm.Contracts`).
- Every test class carries `[Trait("Category", "<Unit|Integration|Architecture|Contract>")]`.
- Redis key shapes, Kafka topic names, headers, and message fields are exactly those in `docs/data-dictionary/redis-and-kafka.md`; GUIDs inside Redis keys are 32 lower-case hex characters (`ToString("N")`).
- Health endpoints `/health/live` and `/health/ready` are anonymous and outside `/v1`; readiness checks SQL Server, Redis, Kafka with a 3 s timeout each; the readiness body is JSON `{ "status": "...", "checks": { "<name>": "<Healthy|Unhealthy|Degraded>" } }`.
- Compose: api on 8080, worker health on 8081, web on 3000, Redis host port `${REDIS_HOST_PORT:-6380}`, SQL image `${MSSQL_IMAGE}`. Local `deploy/.env` uses `mcr.microsoft.com/azure-sql-edge:latest`.
- Integration tests read `MSSQL_IMAGE` (default `mcr.microsoft.com/mssql/server:2022-latest`) and run with `DOCKER_HOST` from `docker context inspect` and `TESTCONTAINERS_RYUK_DISABLED=true` (`make test-integration` sets both).
- Commit subjects follow conventional commits; every commit ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Never edit files under `docs/superpowers/plans/` or `docs/test/` in this plan.

---

## File structure

| Path | Responsibility |
|---|---|
| `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `Crm.sln`, `.dockerignore` | build hygiene |
| `src/Shared/Crm.Kernel/` | `Entity`, `Error`, `Result`, `ITenantContext`, exceptions, `IClock`, `IIdGenerator`, `RedisKeys`, EF `TenantSaveChangesInterceptor`, `ModelBuilderExtensions` |
| `src/Shared/Crm.Contracts/` | message records, `KafkaTopics`, `KafkaHeaders`, `ContractJson` |
| `src/Shared/Crm.Hosting/` | `AddCrmSerilog`, `AddCrmOpenTelemetry`, `AddCrmHealthChecks`, `MapCrmHealth`, `SqlServerHealthCheck`, `RedisHealthCheck`, `KafkaHealthCheck` |
| `src/Modules/<Module>/…` | empty shells: `Add<Module>Module`, `ProfilesDbContext`, `SegmentationDbContext` |
| `src/Hosts/Crm.Api/` | `Program.cs`, `OpenApiExport.cs`, `appsettings*.json`, `Dockerfile` |
| `src/Hosts/Crm.Worker/` | `Program.cs`, `StartupProbeService.cs`, `appsettings*.json`, `Dockerfile` |
| `src/Hosts/Crm.Web/` | Vite React shell, nginx `Dockerfile` |
| `tests/…` | one test project per assembly group; `Crm.IntegrationTests/Fixtures/CrmStackFixture.cs`, `CrmApiFactory.cs` |
| `tests/fixtures/contracts/*.json` | CT-004 snapshots |
| `deploy/docker-compose.yml` | add `api`, `worker`, `web` services |
| `.github/workflows/ci.yml` | four jobs |
| `docs/dev-setup.md` | Podman / Testcontainers notes |

---

### Task 1: Root build files and empty solution

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.dockerignore`, `Crm.sln`, all project files listed below (created with `dotnet new`)

**Interfaces:**
- Produces: project names, root namespaces, and reference graph every later task relies on.

- [ ] **Step 1: Write the root build files**

`global.json`:

```json
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "latestPatch"
  }
}
```

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <!-- Analyzer rules that add noise without value for this codebase; every other recommended rule is an error. -->
    <NoWarn>$(NoWarn);CA1848;CA2007;CA1515;CA1303;CA1031;CA1812;CA1054;CA1056;CA2234;CA1861;CA1062;CA5394</NoWarn>
  </PropertyGroup>
  <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('Tests'))">
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <NoWarn>$(NoWarn);CA1707;CA2000;CA1063;CA1816;CA1001</NoWarn>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <!-- data -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.0.12" />
    <PackageVersion Include="Microsoft.Data.SqlClient" Version="7.0.3" />
    <PackageVersion Include="StackExchange.Redis" Version="3.2.1" />
    <PackageVersion Include="Confluent.Kafka" Version="2.15.1" />
    <!-- hosting -->
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.12" />
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.12" />
    <PackageVersion Include="Scalar.AspNetCore" Version="2.17.3" />
    <PackageVersion Include="Serilog.AspNetCore" Version="10.0.0" />
    <PackageVersion Include="Serilog.Sinks.Console" Version="6.1.1" />
    <PackageVersion Include="Serilog.Formatting.Compact" Version="3.0.0" />
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.18.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.18.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.18.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.18.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.18.0-beta.1" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.18.0" />
    <PackageVersion Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" Version="1.18.0-beta.1" />
    <!-- tests -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.0" />
    <PackageVersion Include="coverlet.collector" Version="10.0.1" />
    <PackageVersion Include="FluentAssertions" Version="7.2.2" />
    <PackageVersion Include="NSubstitute" Version="6.2.0" />
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageVersion Include="Testcontainers" Version="4.15.0" />
    <PackageVersion Include="Testcontainers.Kafka" Version="4.15.0" />
    <PackageVersion Include="Testcontainers.Redis" Version="4.15.0" />
  </ItemGroup>
</Project>
```

`.editorconfig`:

```ini
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
indent_style = space
indent_size = 4
trim_trailing_whitespace = true

[*.{json,yml,yaml,md,ts,tsx,js,css}]
indent_size = 2

[*.cs]
csharp_style_namespace_declarations = file_scoped:warning
csharp_style_var_for_built_in_types = true:suggestion
csharp_style_var_when_type_is_apparent = true:suggestion
csharp_style_var_elsewhere = true:suggestion
csharp_prefer_braces = true:warning
dotnet_sort_system_directives_first = true
dotnet_naming_rule.private_fields_underscore.severity = warning
dotnet_naming_rule.private_fields_underscore.symbols = private_fields
dotnet_naming_rule.private_fields_underscore.style = underscore_camel
dotnet_naming_symbols.private_fields.applicable_kinds = field
dotnet_naming_symbols.private_fields.applicable_accessibilities = private
dotnet_naming_style.underscore_camel.capitalization = camel_case
dotnet_naming_style.underscore_camel.required_prefix = _
dotnet_diagnostic.IDE0005.severity = warning
```

`.dockerignore`:

```
**/bin/
**/obj/
**/node_modules/
**/.git/
.superpowers/
deploy/.env
```

- [ ] **Step 2: Create the solution and every project**

Run from the repository root (fish or bash; the loop syntax is bash):

```bash
dotnet new sln -n Crm

# shared
dotnet new classlib -o src/Shared/Crm.Kernel    -n Crm.Kernel
dotnet new classlib -o src/Shared/Crm.Contracts -n Crm.Contracts
dotnet new classlib -o src/Shared/Crm.Hosting   -n Crm.Hosting

# modules
for m in Ingestion; do
  dotnet new classlib -o src/Modules/$m/$m.Application    -n $m.Application
  dotnet new classlib -o src/Modules/$m/$m.Infrastructure -n $m.Infrastructure
done
for m in Profiles Segmentation Tenancy; do
  dotnet new classlib -o src/Modules/$m/$m.Domain         -n $m.Domain
  dotnet new classlib -o src/Modules/$m/$m.Application    -n $m.Application
  dotnet new classlib -o src/Modules/$m/$m.Infrastructure -n $m.Infrastructure
done

# hosts
dotnet new web    -o src/Hosts/Crm.Api    -n Crm.Api
dotnet new web    -o src/Hosts/Crm.Worker -n Crm.Worker

# tests
for t in Crm.Kernel.UnitTests Crm.Contracts.UnitTests Crm.Api.UnitTests Ingestion.UnitTests Profiles.UnitTests Segmentation.UnitTests Tenancy.UnitTests Crm.ArchTests Crm.IntegrationTests; do
  dotnet new xunit -o tests/$t -n $t
  rm tests/$t/UnitTest1.cs
done

dotnet sln Crm.sln add $(find src tests -name '*.csproj')
```

Then delete every `Class1.cs` the classlib template created: `find src -name Class1.cs -delete`.

- [ ] **Step 3: Rewrite every csproj with root namespace, references, and packages**

Replace the generated files with exactly these contents (package versions come from `Directory.Packages.props`, so `PackageReference` has no `Version`).

`src/Shared/Crm.Kernel/Crm.Kernel.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Crm.Kernel</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
  </ItemGroup>
</Project>
```

`src/Shared/Crm.Contracts/Crm.Contracts.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Crm.Contracts</RootNamespace>
  </PropertyGroup>
</Project>
```

`src/Shared/Crm.Hosting/Crm.Hosting.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Crm.Hosting</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Crm.Kernel\Crm.Kernel.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" />
    <PackageReference Include="StackExchange.Redis" />
    <PackageReference Include="Confluent.Kafka" />
    <PackageReference Include="Serilog.AspNetCore" />
    <PackageReference Include="Serilog.Sinks.Console" />
    <PackageReference Include="Serilog.Formatting.Compact" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
    <PackageReference Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
    <PackageReference Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" />
  </ItemGroup>
</Project>
```

`src/Modules/Ingestion/Ingestion.Application/Ingestion.Application.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Crm.Ingestion.Application</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\Shared\Crm.Kernel\Crm.Kernel.csproj" />
    <ProjectReference Include="..\..\..\Shared\Crm.Contracts\Crm.Contracts.csproj" />
  </ItemGroup>
</Project>
```

`src/Modules/Ingestion/Ingestion.Infrastructure/Ingestion.Infrastructure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Crm.Ingestion.Infrastructure</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Ingestion.Application\Ingestion.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Confluent.Kafka" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
  </ItemGroup>
</Project>
```

For each of `Profiles`, `Segmentation`, `Tenancy` (replace `Profiles` with the module name in path, file name, and namespace):

`src/Modules/Profiles/Profiles.Domain/Profiles.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Crm.Profiles.Domain</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\Shared\Crm.Kernel\Crm.Kernel.csproj" />
  </ItemGroup>
</Project>
```

`src/Modules/Profiles/Profiles.Application/Profiles.Application.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Crm.Profiles.Application</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Profiles.Domain\Profiles.Domain.csproj" />
    <ProjectReference Include="..\..\..\Shared\Crm.Contracts\Crm.Contracts.csproj" />
  </ItemGroup>
</Project>
```

`src/Modules/Profiles/Profiles.Infrastructure/Profiles.Infrastructure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Crm.Profiles.Infrastructure</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Profiles.Application\Profiles.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="StackExchange.Redis" />
  </ItemGroup>
</Project>
```

`src/Hosts/Crm.Api/Crm.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <RootNamespace>Crm.Api</RootNamespace>
    <UserSecretsId>crm-api</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Shared\Crm.Hosting\Crm.Hosting.csproj" />
    <ProjectReference Include="..\..\Shared\Crm.Kernel\Crm.Kernel.csproj" />
    <ProjectReference Include="..\..\Shared\Crm.Contracts\Crm.Contracts.csproj" />
    <ProjectReference Include="..\..\Modules\Ingestion\Ingestion.Infrastructure\Ingestion.Infrastructure.csproj" />
    <ProjectReference Include="..\..\Modules\Profiles\Profiles.Infrastructure\Profiles.Infrastructure.csproj" />
    <ProjectReference Include="..\..\Modules\Segmentation\Segmentation.Infrastructure\Segmentation.Infrastructure.csproj" />
    <ProjectReference Include="..\..\Modules\Tenancy\Tenancy.Infrastructure\Tenancy.Infrastructure.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Scalar.AspNetCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
  </ItemGroup>
</Project>
```

`src/Hosts/Crm.Worker/Crm.Worker.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <RootNamespace>Crm.Worker</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Shared\Crm.Hosting\Crm.Hosting.csproj" />
    <ProjectReference Include="..\..\Shared\Crm.Kernel\Crm.Kernel.csproj" />
    <ProjectReference Include="..\..\Shared\Crm.Contracts\Crm.Contracts.csproj" />
    <ProjectReference Include="..\..\Modules\Profiles\Profiles.Infrastructure\Profiles.Infrastructure.csproj" />
    <ProjectReference Include="..\..\Modules\Segmentation\Segmentation.Infrastructure\Segmentation.Infrastructure.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Confluent.Kafka" />
  </ItemGroup>
</Project>
```

Test projects. Every test csproj shares this body; only `RootNamespace` and the `ProjectReference`/extra `PackageReference` items differ:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>ROOT_NAMESPACE</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="NSubstitute" />
  </ItemGroup>
  <ItemGroup>
    <!-- project references below -->
  </ItemGroup>
</Project>
```

| Project | RootNamespace | ProjectReference items | Extra PackageReference |
|---|---|---|---|
| `tests/Crm.Kernel.UnitTests` | `Crm.Kernel.UnitTests` | `..\..\src\Shared\Crm.Kernel\Crm.Kernel.csproj` | `Microsoft.EntityFrameworkCore.InMemory` |
| `tests/Crm.Contracts.UnitTests` | `Crm.Contracts.UnitTests` | `..\..\src\Shared\Crm.Contracts\Crm.Contracts.csproj` | — |
| `tests/Crm.Api.UnitTests` | `Crm.Api.UnitTests` | `..\..\src\Hosts\Crm.Api\Crm.Api.csproj` | — |
| `tests/Ingestion.UnitTests` | `Crm.Ingestion.UnitTests` | `..\..\src\Modules\Ingestion\Ingestion.Application\Ingestion.Application.csproj` | — |
| `tests/Profiles.UnitTests` | `Crm.Profiles.UnitTests` | `..\..\src\Modules\Profiles\Profiles.Application\Profiles.Application.csproj` | — |
| `tests/Segmentation.UnitTests` | `Crm.Segmentation.UnitTests` | `..\..\src\Modules\Segmentation\Segmentation.Application\Segmentation.Application.csproj` | — |
| `tests/Tenancy.UnitTests` | `Crm.Tenancy.UnitTests` | `..\..\src\Modules\Tenancy\Tenancy.Application\Tenancy.Application.csproj` | — |
| `tests/Crm.ArchTests` | `Crm.ArchTests` | `Crm.Api.csproj`, `Crm.Worker.csproj`, `Crm.Hosting.csproj`, and all 11 module csproj files | `NetArchTest.Rules`, `Microsoft.EntityFrameworkCore.SqlServer` |
| `tests/Crm.IntegrationTests` | `Crm.IntegrationTests` | `Crm.Api.csproj`, `Crm.Worker.csproj` | `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers`, `Testcontainers.Kafka`, `Testcontainers.Redis`, `Microsoft.Data.SqlClient` |

Also add to `tests/Crm.IntegrationTests/Crm.IntegrationTests.csproj` a `<Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />` and create `tests/Crm.IntegrationTests/xunit.runner.json`:

```json
{ "$schema": "https://xunit.net/schema/current/xunit.runner.schema.json", "parallelizeTestCollections": false }
```

- [ ] **Step 4: Give every module and shared assembly a marker type so empty projects compile and can be referenced by name**

Create `AssemblyMarker.cs` in each of the 11 module projects, `Crm.Kernel`, `Crm.Contracts`, `Crm.Hosting`, with the matching namespace, e.g. `src/Modules/Profiles/Profiles.Domain/AssemblyMarker.cs`:

```csharp
namespace Crm.Profiles.Domain;

/// <summary>Anchor type for assembly-level lookups (architecture tests, scanning).</summary>
public static class AssemblyMarker;
```

Replace `src/Hosts/Crm.Api/Program.cs` and `src/Hosts/Crm.Worker/Program.cs` with the minimal placeholder (Tasks 7 and 8 replace them):

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok());
await app.RunAsync();

public partial class Program;
```

Delete the template's `appsettings.Development.json` is **not** needed — keep both appsettings files as generated for now.

- [ ] **Step 5: Build and verify zero warnings**

Run: `dotnet build Crm.sln -warnaserror`
Expected: `Build succeeded.` with `0 Warning(s)`. Then `dotnet test Crm.sln` — expected: every test project reports `No test is available` (exit code 0 requires the projects to build; empty test assemblies are fine).

If restore fails on the two `-beta.1` OpenTelemetry packages, check the exact string `1.18.0-beta.1` was copied without typos; central package management accepts prerelease versions as written.

- [ ] **Step 6: Commit**

```bash
git add global.json Directory.Build.props Directory.Packages.props .editorconfig .dockerignore Crm.sln src tests
git commit -m "build: scaffold solution, projects, and central package versions

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Crm.Kernel core types (Entity, Error, Result, tenant context, clock, ids, Redis keys)

**Files:**
- Create: `src/Shared/Crm.Kernel/Entity.cs`, `Error.cs`, `Result.cs`, `Tenancy/ITenantContext.cs`, `Tenancy/TenantContext.cs`, `Tenancy/TenantContextMissingException.cs`, `Tenancy/TenantMismatchException.cs`, `Time/IClock.cs`, `Ids/IIdGenerator.cs`, `Redis/RedisKeys.cs`
- Test: `tests/Crm.Kernel.UnitTests/ResultTests.cs`, `tests/Crm.Kernel.UnitTests/TenantContextTests.cs`, `tests/Crm.Kernel.UnitTests/RedisKeysTests.cs`

**Interfaces:**
- Produces: `Crm.Kernel.Entity`, `Crm.Kernel.Error`, `Crm.Kernel.ErrorKind`, `Crm.Kernel.Result`, `Crm.Kernel.Result<T>`, `Crm.Kernel.Tenancy.ITenantContext`, `TenantContext`, `TenantPrincipalKind`, `TenantContextMissingException`, `TenantMismatchException`, `Crm.Kernel.Time.IClock`, `SystemClock`, `Crm.Kernel.Ids.IIdGenerator`, `UuidV7IdGenerator`, `Crm.Kernel.Redis.RedisKeys` — signatures below are used verbatim by later tasks.

- [ ] **Step 1: Write the failing tests**

`tests/Crm.Kernel.UnitTests/ResultTests.cs`:

```csharp
using Crm.Kernel;
using FluentAssertions;

namespace Crm.Kernel.UnitTests;

[Trait("Category", "Unit")]
public class ResultTests
{
    [Fact]
    public void Ok_carries_value_and_no_error()
    {
        Result<int> result = Result.Ok(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Fail_carries_error_and_value_access_throws()
    {
        var error = Error.NotFound("segment.missing", "Segment not found");
        Result<int> result = Result.Fail<int>(error);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
        var act = () => result.Value;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Match_selects_branch()
    {
        Result<string>.Ok("a").Match(v => v + "!", e => e.Code).Should().Be("a!");
        Result<string>.Fail(Error.Conflict("dup", "dup")).Match(v => v, e => e.Code).Should().Be("dup");
    }

    [Fact]
    public void Implicit_conversions_from_value_and_error()
    {
        Result<int> ok = 7;
        Result<int> fail = Error.Validation("bad", "bad", new Dictionary<string, string[]> { ["x"] = ["required"] });

        ok.IsSuccess.Should().BeTrue();
        fail.Error!.Kind.Should().Be(ErrorKind.Validation);
        fail.Error.Fields!["x"].Should().ContainSingle("required");
    }
}
```

`tests/Crm.Kernel.UnitTests/TenantContextTests.cs`:

```csharp
using Crm.Kernel.Tenancy;
using FluentAssertions;

namespace Crm.Kernel.UnitTests;

[Trait("Category", "Unit")]
public class TenantContextTests
{
    [Fact]
    public void Unresolved_context_throws_on_TenantId()
    {
        var ctx = new TenantContext();

        ctx.IsResolved.Should().BeFalse();
        ctx.Kind.Should().Be(TenantPrincipalKind.None);
        var act = () => ctx.TenantId;
        act.Should().Throw<TenantContextMissingException>();
    }

    [Fact]
    public void Resolve_sets_all_fields()
    {
        var ctx = new TenantContext();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        ctx.Resolve(tenant, user, TenantPrincipalKind.User);

        ctx.IsResolved.Should().BeTrue();
        ctx.TenantId.Should().Be(tenant);
        ctx.UserId.Should().Be(user);
        ctx.Kind.Should().Be(TenantPrincipalKind.User);
    }

    [Fact]
    public void Resolve_twice_throws()
    {
        var ctx = new TenantContext();
        ctx.Resolve(Guid.NewGuid(), null, TenantPrincipalKind.ApiKey);

        var act = () => ctx.Resolve(Guid.NewGuid(), null, TenantPrincipalKind.ApiKey);

        act.Should().Throw<InvalidOperationException>();
    }
}
```

`tests/Crm.Kernel.UnitTests/RedisKeysTests.cs` (UT-057):

```csharp
using Crm.Kernel.Redis;
using FluentAssertions;

namespace Crm.Kernel.UnitTests;

[Trait("Category", "Unit")]
public class RedisKeysTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Segment = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Profile = Guid.Parse("99999999-8888-7777-6666-555555555555");

    [Fact]
    public void Identity_cache_key_shape()
        => RedisKeys.IdentityCache(Tenant, "email", "a@b.com")
            .Should().Be("t:11111111222233334444555555555555:id:email:a@b.com");

    [Fact]
    public void Segment_members_key_shape()
        => RedisKeys.SegmentMembers(Tenant, Segment)
            .Should().Be("t:11111111222233334444555555555555:seg:aaaaaaaabbbbccccddddeeeeeeeeeeee:members");

    [Fact]
    public void Event_counter_key_includes_event_name_and_window()
        => RedisKeys.EventCounter(Tenant, Segment, "purchase", "30d", Profile)
            .Should().Be("t:11111111222233334444555555555555:seg:aaaaaaaabbbbccccddddeeeeeeeeeeee:ev:purchase:30d:99999999888877776666555555555555");

    [Fact]
    public void Preview_key_shape()
        => RedisKeys.PreviewCount(Tenant, "abc123").Should().Be("t:11111111222233334444555555555555:preview:abc123");

    [Fact]
    public void Api_key_cache_and_channel_are_the_only_unprefixed_keys()
    {
        RedisKeys.ApiKey("AbCdEfGh").Should().Be("apikey:AbCdEfGh");
        RedisKeys.SegmentsChangedChannel.Should().Be("segments:changed");
    }

    [Fact]
    public void Every_tenant_scoped_builder_starts_with_tenant_prefix()
    {
        var prefix = "t:11111111222233334444555555555555:";
        new[]
        {
            RedisKeys.IdentityCache(Tenant, "phone", "+62"),
            RedisKeys.SegmentMembers(Tenant, Segment),
            RedisKeys.EventCounter(Tenant, Segment, "x", "7d", Profile),
            RedisKeys.PreviewCount(Tenant, "h"),
        }.Should().OnlyContain(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Crm.Kernel.UnitTests`
Expected: build errors — `Result`, `Error`, `TenantContext`, `RedisKeys` do not exist.

- [ ] **Step 3: Implement the types**

`src/Shared/Crm.Kernel/Entity.cs`:

```csharp
namespace Crm.Kernel;

/// <summary>Base for every tenant-scoped persistent object. Ids are UUIDv7 so they sort by creation time.</summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    public Guid TenantId { get; protected internal set; }

    public DateTime CreatedAt { get; protected internal set; }

    public DateTime UpdatedAt { get; protected internal set; }
}
```

`src/Shared/Crm.Kernel/Error.cs`:

```csharp
namespace Crm.Kernel;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Unauthorized,
    Unexpected,
}

public sealed record Error(
    ErrorKind Kind,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? fields = null)
        => new(ErrorKind.Validation, code, message, fields);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);

    public static Error Unauthorized(string code, string message) => new(ErrorKind.Unauthorized, code, message);

    public static Error Unexpected(string code, string message) => new(ErrorKind.Unexpected, code, message);
}
```

`src/Shared/Crm.Kernel/Result.cs`:

```csharp
namespace Crm.Kernel;

public class Result
{
    protected Result(bool isSuccess, Error? error)
    {
        if (isSuccess && error is not null)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && error is null)
        {
            throw new ArgumentException("A failed result requires an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    public static Result Ok() => new(true, null);

    public static Result Fail(Error error) => new(false, error);

    public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);

    public static Result<T> Fail<T>(Error error) => Result<T>.Fail(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, Error? error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure ({Error!.Code}); Value is unavailable.");

    public static Result<T> Ok(T value) => new(true, value, null);

    public static new Result<T> Fail(Error error) => new(false, default, error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
        => IsSuccess ? onSuccess(_value!) : onFailure(Error!);

    public static implicit operator Result<T>(T value) => Ok(value);

    public static implicit operator Result<T>(Error error) => Fail(error);
}
```

`src/Shared/Crm.Kernel/Tenancy/ITenantContext.cs`:

```csharp
namespace Crm.Kernel.Tenancy;

public enum TenantPrincipalKind
{
    None,
    User,
    ApiKey,
}

/// <summary>The tenant the current request or message is acting for. Scoped per request / per consumed message.</summary>
public interface ITenantContext
{
    bool IsResolved { get; }

    /// <summary>Throws <see cref="TenantContextMissingException"/> when unresolved. Never returns Guid.Empty.</summary>
    Guid TenantId { get; }

    Guid? UserId { get; }

    TenantPrincipalKind Kind { get; }
}
```

`src/Shared/Crm.Kernel/Tenancy/TenantContext.cs`:

```csharp
namespace Crm.Kernel.Tenancy;

public sealed class TenantContext : ITenantContext
{
    private Guid _tenantId;

    public bool IsResolved { get; private set; }

    public Guid TenantId => IsResolved ? _tenantId : throw new TenantContextMissingException();

    public Guid? UserId { get; private set; }

    public TenantPrincipalKind Kind { get; private set; } = TenantPrincipalKind.None;

    public void Resolve(Guid tenantId, Guid? userId, TenantPrincipalKind kind)
    {
        if (IsResolved)
        {
            throw new InvalidOperationException("Tenant context is already resolved for this scope.");
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));
        }

        _tenantId = tenantId;
        UserId = userId;
        Kind = kind;
        IsResolved = true;
    }
}
```

`src/Shared/Crm.Kernel/Tenancy/TenantContextMissingException.cs`:

```csharp
namespace Crm.Kernel.Tenancy;

/// <summary>Raised when data access happens without a resolved tenant. Always a bug: the caller failed closed instead of leaking data.</summary>
public sealed class TenantContextMissingException : InvalidOperationException
{
    public TenantContextMissingException()
        : base("No tenant is resolved for the current scope; refusing to run a tenant-scoped operation.")
    {
    }
}
```

`src/Shared/Crm.Kernel/Tenancy/TenantMismatchException.cs`:

```csharp
namespace Crm.Kernel.Tenancy;

public sealed class TenantMismatchException : InvalidOperationException
{
    public TenantMismatchException(Guid expected, Guid actual, string entityType)
        : base($"Entity {entityType} belongs to tenant {actual} but the current scope is tenant {expected}.")
    {
        Expected = expected;
        Actual = actual;
    }

    public Guid Expected { get; }

    public Guid Actual { get; }
}
```

`src/Shared/Crm.Kernel/Time/IClock.cs`:

```csharp
namespace Crm.Kernel.Time;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
```

`src/Shared/Crm.Kernel/Ids/IIdGenerator.cs`:

```csharp
namespace Crm.Kernel.Ids;

public interface IIdGenerator
{
    Guid NewId();
}

public sealed class UuidV7IdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}
```

`src/Shared/Crm.Kernel/Redis/RedisKeys.cs`:

```csharp
namespace Crm.Kernel.Redis;

/// <summary>
/// The complete Redis key inventory for slice 1 (docs/data-dictionary/redis-and-kafka.md).
/// Every tenant-scoped key starts with t:{tenantId}:; the two exceptions are ApiKey and SegmentsChangedChannel.
/// </summary>
public static class RedisKeys
{
    public const string SegmentsChangedChannel = "segments:changed";

    public static string IdentityCache(Guid tenantId, string kind, string value)
        => $"t:{Hex(tenantId)}:id:{kind}:{value}";

    public static string SegmentMembers(Guid tenantId, Guid segmentId)
        => $"t:{Hex(tenantId)}:seg:{Hex(segmentId)}:members";

    public static string EventCounter(Guid tenantId, Guid segmentId, string eventName, string window, Guid profileId)
        => $"t:{Hex(tenantId)}:seg:{Hex(segmentId)}:ev:{eventName}:{window}:{Hex(profileId)}";

    public static string PreviewCount(Guid tenantId, string definitionHashHex)
        => $"t:{Hex(tenantId)}:preview:{definitionHashHex}";

    public static string ApiKey(string prefix) => $"apikey:{prefix}";

    private static string Hex(Guid id) => id.ToString("N");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Crm.Kernel.UnitTests`
Expected: `Passed! - Failed: 0, Passed: 13`.

- [ ] **Step 5: Commit**

```bash
git add src/Shared/Crm.Kernel tests/Crm.Kernel.UnitTests
git commit -m "feat(kernel): add Entity, Result, tenant context, clock, id generator, Redis keys

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Crm.Kernel EF Core tenant enforcement (interceptor and query filters)

**Files:**
- Create: `src/Shared/Crm.Kernel/Data/TenantSaveChangesInterceptor.cs`, `src/Shared/Crm.Kernel/Data/ModelBuilderExtensions.cs`
- Test: `tests/Crm.Kernel.UnitTests/Data/TenantEnforcementTests.cs`

**Interfaces:**
- Consumes: `Entity`, `ITenantContext`, `IClock`, `TenantMismatchException`, `TenantContextMissingException` (Task 2).
- Produces: `Crm.Kernel.Data.TenantSaveChangesInterceptor(ITenantContext, IClock)` (register as scoped, add via `options.AddInterceptors(...)`); `Crm.Kernel.Data.ITenantScopedDbContext { Guid CurrentTenantId }`; `Crm.Kernel.Data.ModelBuilderExtensions.ApplyTenantFilters<TContext>(this ModelBuilder, TContext context) where TContext : DbContext, ITenantScopedDbContext`. The filter lambda references the DbContext instance (not the ITenantContext) because EF Core caches the model per context type and only re-binds expressions that reference the current DbContext; capturing the ITenantContext directly would freeze the first instance's tenant into the cached model (found during execution, corrected in the fix round of this task).

- [ ] **Step 1: Write the failing tests (UT-054, UT-055)**

`tests/Crm.Kernel.UnitTests/Data/TenantEnforcementTests.cs`:

```csharp
using Crm.Kernel;
using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Crm.Kernel.UnitTests.Data;

[Trait("Category", "Unit")]
public class TenantEnforcementTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    public sealed class Widget : Entity
    {
        public string Name { get; set; } = string.Empty;

        public void ForceTenant(Guid tenantId) => TenantId = tenantId;
    }

    public sealed class WidgetDbContext(DbContextOptions<WidgetDbContext> options, ITenantContext tenant)
        : DbContext(options), ITenantScopedDbContext
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        public Guid CurrentTenantId => tenant.TenantId;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>();
            modelBuilder.ApplyTenantFilters(this);
        }
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }

    private static WidgetDbContext CreateContext(TenantContext tenant, string dbName)
    {
        var options = new DbContextOptionsBuilder<WidgetDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(new TenantSaveChangesInterceptor(tenant, new FixedClock(Now)))
            .Options;
        return new WidgetDbContext(options, tenant);
    }

    private static TenantContext Resolved(Guid tenantId)
    {
        var ctx = new TenantContext();
        ctx.Resolve(tenantId, null, TenantPrincipalKind.User);
        return ctx;
    }

    [Fact]
    public async Task UT054_query_without_resolved_tenant_throws_before_returning_rows()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var seeded = CreateContext(Resolved(TenantA), dbName))
        {
            seeded.Widgets.Add(new Widget { Name = "a" });
            await seeded.SaveChangesAsync();
        }

        await using var unresolved = CreateContext(new TenantContext(), dbName);
        var act = async () => await unresolved.Widgets.ToListAsync();

        await act.Should().ThrowAsync<TenantContextMissingException>();
    }

    [Fact]
    public async Task UT055_added_entity_is_stamped_with_tenant_and_timestamps()
    {
        await using var ctx = CreateContext(Resolved(TenantA), Guid.NewGuid().ToString());
        var widget = new Widget { Name = "w" };

        ctx.Widgets.Add(widget);
        await ctx.SaveChangesAsync();

        widget.TenantId.Should().Be(TenantA);
        widget.CreatedAt.Should().Be(Now);
        widget.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task UT055_modified_entity_of_another_tenant_throws()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var asB = CreateContext(Resolved(TenantB), dbName))
        {
            asB.Widgets.Add(new Widget { Name = "b" });
            await asB.SaveChangesAsync();
        }

        await using var asA = CreateContext(Resolved(TenantA), dbName);
        var foreign = await asA.Widgets.IgnoreQueryFilters().SingleAsync();
        foreign.Name = "hijacked";
        var act = async () => await asA.SaveChangesAsync();

        await act.Should().ThrowAsync<TenantMismatchException>();
    }

    [Fact]
    public async Task UT055_added_entity_pre_stamped_with_another_tenant_throws()
    {
        await using var ctx = CreateContext(Resolved(TenantA), Guid.NewGuid().ToString());
        var widget = new Widget { Name = "x" };
        widget.ForceTenant(TenantB);

        ctx.Widgets.Add(widget);
        var act = async () => await ctx.SaveChangesAsync();

        await act.Should().ThrowAsync<TenantMismatchException>();
    }

    [Fact]
    public async Task Query_filter_hides_other_tenants_rows()
    {
        var dbName = Guid.NewGuid().ToString();
        await using (var asA = CreateContext(Resolved(TenantA), dbName))
        {
            asA.Widgets.Add(new Widget { Name = "a" });
            await asA.SaveChangesAsync();
        }

        await using (var asB = CreateContext(Resolved(TenantB), dbName))
        {
            asB.Widgets.Add(new Widget { Name = "b" });
            await asB.SaveChangesAsync();
        }

        await using var reader = CreateContext(Resolved(TenantA), dbName);
        var names = await reader.Widgets.Select(w => w.Name).ToListAsync();

        names.Should().Equal("a");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Crm.Kernel.UnitTests --filter FullyQualifiedName~TenantEnforcementTests`
Expected: build errors — `TenantSaveChangesInterceptor` and `ApplyTenantFilters` do not exist.

- [ ] **Step 3: Implement**

`src/Shared/Crm.Kernel/Data/TenantSaveChangesInterceptor.cs`:

```csharp
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Crm.Kernel.Data;

/// <summary>
/// Stamps TenantId/CreatedAt/UpdatedAt on inserts and refuses writes to rows of another tenant.
/// Register as scoped and attach with DbContextOptionsBuilder.AddInterceptors.
/// </summary>
public sealed class TenantSaveChangesInterceptor(ITenantContext tenant, IClock clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var entries = context.ChangeTracker
            .Entries<Entity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        if (entries.Count == 0)
        {
            return;
        }

        var tenantId = tenant.TenantId;
        var now = clock.UtcNow;

        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.TenantId != Guid.Empty && entry.Entity.TenantId != tenantId)
                    {
                        throw Mismatch(entry, tenantId);
                    }

                    entry.Entity.TenantId = tenantId;
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Modified:
                    if (entry.Entity.TenantId != tenantId)
                    {
                        throw Mismatch(entry, tenantId);
                    }

                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Deleted:
                    if (entry.Entity.TenantId != tenantId)
                    {
                        throw Mismatch(entry, tenantId);
                    }

                    break;
            }
        }
    }

    private static TenantMismatchException Mismatch(EntityEntry<Entity> entry, Guid expected)
        => new(expected, entry.Entity.TenantId, entry.Metadata.ClrType.Name);
}
```

`src/Shared/Crm.Kernel/Data/ITenantScopedDbContext.cs`:

```csharp
namespace Crm.Kernel.Data;

/// <summary>
/// Implemented by every module DbContext. Query filters read the tenant through the context instance
/// so EF Core re-evaluates them per query instead of caching the first instance's tenant in the model.
/// </summary>
public interface ITenantScopedDbContext
{
    /// <summary>Throws <see cref="Tenancy.TenantContextMissingException"/> when no tenant is resolved.</summary>
    Guid CurrentTenantId { get; }
}
```

`src/Shared/Crm.Kernel/Data/ModelBuilderExtensions.cs`:

```csharp
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Crm.Kernel.Data;

public static class ModelBuilderExtensions
{
    private static readonly MethodInfo SetFilterMethod = typeof(ModelBuilderExtensions)
        .GetMethod(nameof(SetFilter), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Adds the global query filter e => e.TenantId == context.CurrentTenantId to every root entity type derived from
    /// <see cref="Entity"/>. The lambda references the DbContext instance, which EF Core substitutes with the
    /// current context at query time; an unresolved tenant therefore throws <see cref="Tenancy.TenantContextMissingException"/>
    /// on every query, so reads fail closed.
    /// </summary>
    public static ModelBuilder ApplyTenantFilters<TContext>(this ModelBuilder modelBuilder, TContext context)
        where TContext : DbContext, ITenantScopedDbContext
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.BaseType is not null || !typeof(Entity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            SetFilterMethod.MakeGenericMethod(entityType.ClrType, typeof(TContext)).Invoke(null, [modelBuilder, context]);
        }

        return modelBuilder;
    }

    private static void SetFilter<TEntity, TContext>(ModelBuilder modelBuilder, TContext context)
        where TEntity : Entity
        where TContext : DbContext, ITenantScopedDbContext
        => modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == context.CurrentTenantId);
}
```

The test's `WidgetDbContext` implements `ITenantScopedDbContext` (`public Guid CurrentTenantId => tenant.TenantId;`) and calls `modelBuilder.ApplyTenantFilters(this);`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Crm.Kernel.UnitTests`
Expected: `Passed! - Failed: 0, Passed: 18`.

- [ ] **Step 5: Commit**

```bash
git add src/Shared/Crm.Kernel/Data tests/Crm.Kernel.UnitTests/Data
git commit -m "feat(kernel): enforce tenant scope with EF save interceptor and query filters

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Crm.Contracts messages, topics, headers, and snapshot tests (CT-004)

**Files:**
- Create: `src/Shared/Crm.Contracts/Messages.cs`, `src/Shared/Crm.Contracts/KafkaTopics.cs`, `src/Shared/Crm.Contracts/KafkaHeaders.cs`, `src/Shared/Crm.Contracts/ContractJson.cs`
- Create: `tests/fixtures/contracts/TrackEventRaw.json`, `IdentifyRaw.json`, `EventTracked.json`, `ProfileUpdated.json`, `ProfileMerged.json`
- Test: `tests/Crm.Contracts.UnitTests/ContractSnapshotTests.cs`

**Interfaces:**
- Produces: records `Crm.Contracts.TrackEventRaw`, `IdentifyRaw`, `EventTracked`, `ProfileUpdated`, `ProfileMerged`, `ProfileSnapshot`; `Crm.Contracts.KafkaTopics.EventsRaw/EventsProcessed/EventsDlq/Partitions`; `Crm.Contracts.KafkaHeaders.*`; `Crm.Contracts.ContractJson.Options`.

- [ ] **Step 1: Write the fixture files**

`tests/fixtures/contracts/TrackEventRaw.json`:

```json
{
  "eventId": "0192b1c4-0000-7000-8000-000000000001",
  "tenantId": "11111111-2222-3333-4444-555555555555",
  "userId": "u-123",
  "event": "purchase",
  "properties": {
    "amount": 49.9,
    "sku": "A1"
  },
  "occurredAt": "2026-09-16T08:00:00Z",
  "receivedAt": "2026-09-16T08:00:01Z"
}
```

`tests/fixtures/contracts/IdentifyRaw.json`:

```json
{
  "eventId": "0192b1c4-0000-7000-8000-000000000002",
  "tenantId": "11111111-2222-3333-4444-555555555555",
  "userId": "u-123",
  "anonymousId": "anon-9f1",
  "traits": {
    "plan": "pro",
    "country": "ID"
  },
  "receivedAt": "2026-09-16T08:00:01Z"
}
```

`tests/fixtures/contracts/EventTracked.json`:

```json
{
  "eventId": "0192b1c4-0000-7000-8000-000000000001",
  "tenantId": "11111111-2222-3333-4444-555555555555",
  "profileId": "0192b1c4-0000-7000-8000-00000000aaaa",
  "event": "purchase",
  "properties": {
    "amount": 49.9,
    "sku": "A1"
  },
  "occurredAt": "2026-09-16T08:00:00Z",
  "profile": {
    "externalId": "u-123",
    "email": "a@b.com",
    "attributes": {
      "plan": "pro"
    }
  }
}
```

`tests/fixtures/contracts/ProfileUpdated.json`:

```json
{
  "tenantId": "11111111-2222-3333-4444-555555555555",
  "profileId": "0192b1c4-0000-7000-8000-00000000aaaa",
  "changedKeys": [
    "plan",
    "country"
  ],
  "profile": {
    "externalId": "u-123",
    "email": "a@b.com",
    "attributes": {
      "plan": "pro",
      "country": "ID"
    }
  },
  "occurredAt": "2026-09-16T08:00:01Z"
}
```

`tests/fixtures/contracts/ProfileMerged.json`:

```json
{
  "tenantId": "11111111-2222-3333-4444-555555555555",
  "winnerProfileId": "0192b1c4-0000-7000-8000-00000000aaaa",
  "loserProfileId": "0192b1c4-0000-7000-8000-00000000bbbb",
  "occurredAt": "2026-09-16T08:00:02Z"
}
```

- [ ] **Step 2: Write the failing snapshot test**

`tests/Crm.Contracts.UnitTests/ContractSnapshotTests.cs`:

```csharp
using System.Text.Json;
using Crm.Contracts;
using FluentAssertions;

namespace Crm.Contracts.UnitTests;

[Trait("Category", "Contract")]
public class ContractSnapshotTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid EventId1 = Guid.Parse("0192b1c4-0000-7000-8000-000000000001");
    private static readonly Guid EventId2 = Guid.Parse("0192b1c4-0000-7000-8000-000000000002");
    private static readonly Guid Winner = Guid.Parse("0192b1c4-0000-7000-8000-00000000aaaa");
    private static readonly Guid Loser = Guid.Parse("0192b1c4-0000-7000-8000-00000000bbbb");
    private static readonly DateTime T0 = new(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static readonly ProfileSnapshot Snapshot = new("u-123", "a@b.com", null, Json("""{"plan":"pro"}"""));

    public static TheoryData<string, object> Cases => new()
    {
        { "TrackEventRaw", new TrackEventRaw(EventId1, Tenant, "u-123", null, "purchase", Json("""{"amount":49.9,"sku":"A1"}"""), T0, T0.AddSeconds(1)) },
        { "IdentifyRaw", new IdentifyRaw(EventId2, Tenant, "u-123", "anon-9f1", Json("""{"plan":"pro","country":"ID"}"""), T0.AddSeconds(1)) },
        { "EventTracked", new EventTracked(EventId1, Tenant, Winner, "purchase", Json("""{"amount":49.9,"sku":"A1"}"""), T0, Snapshot) },
        { "ProfileUpdated", new ProfileUpdated(Tenant, Winner, ["plan", "country"], Snapshot with { Attributes = Json("""{"plan":"pro","country":"ID"}""") }, T0.AddSeconds(1)) },
        { "ProfileMerged", new ProfileMerged(Tenant, Winner, Loser, T0.AddSeconds(2)) },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Serialized_message_matches_committed_snapshot(string name, object message)
    {
        var path = Path.Combine(FixturesRoot(), "contracts", $"{name}.json");
        var expected = Normalize(File.ReadAllText(path));

        var actual = Normalize(JsonSerializer.Serialize(message, message.GetType(), ContractJson.IndentedOptions));

        actual.Should().Be(expected, $"regenerate deliberately if the contract changed: {path}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Snapshot_round_trips_through_deserialization(string name, object message)
    {
        var path = Path.Combine(FixturesRoot(), "contracts", $"{name}.json");

        var roundTripped = JsonSerializer.Deserialize(File.ReadAllText(path), message.GetType(), ContractJson.Options);

        JsonSerializer.Serialize(roundTripped, message.GetType(), ContractJson.Options)
            .Should().Be(JsonSerializer.Serialize(message, message.GetType(), ContractJson.Options));
    }

    [Fact]
    public void Topic_and_header_constants_match_the_data_dictionary()
    {
        KafkaTopics.EventsRaw.Should().Be("events.raw");
        KafkaTopics.EventsProcessed.Should().Be("events.processed");
        KafkaTopics.EventsDlq.Should().Be("events.dlq");
        KafkaTopics.Partitions.Should().Be(12);
        KafkaHeaders.TenantId.Should().Be("tenant-id");
        KafkaHeaders.MessageType.Should().Be("message-type");
        KafkaHeaders.TraceParent.Should().Be("traceparent");
        KafkaHeaders.DlqError.Should().Be("error");
        KafkaHeaders.DlqAttempt.Should().Be("attempt");
        KafkaHeaders.DlqSourceOffset.Should().Be("source-offset");
    }

    private static string Normalize(string json) => json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static string FixturesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new DirectoryNotFoundException("tests/fixtures not found above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "tests", "fixtures");
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Crm.Contracts.UnitTests`
Expected: build errors — contract types do not exist.

- [ ] **Step 4: Implement**

`src/Shared/Crm.Contracts/Messages.cs`:

```csharp
using System.Text.Json;

namespace Crm.Contracts;

/// <summary>Profile fields carried on processed events so consumers never read the database for evaluation.</summary>
public sealed record ProfileSnapshot(string? ExternalId, string? Email, string? Phone, JsonElement Attributes);

/// <summary>events.raw — a track call accepted by the API.</summary>
public sealed record TrackEventRaw(
    Guid EventId,
    Guid TenantId,
    string? UserId,
    string? AnonymousId,
    string Event,
    JsonElement Properties,
    DateTime OccurredAt,
    DateTime ReceivedAt);

/// <summary>events.raw — an identify call accepted by the API.</summary>
public sealed record IdentifyRaw(
    Guid EventId,
    Guid TenantId,
    string UserId,
    string? AnonymousId,
    JsonElement Traits,
    DateTime ReceivedAt);

/// <summary>events.processed — an event appended to a profile timeline.</summary>
public sealed record EventTracked(
    Guid EventId,
    Guid TenantId,
    Guid ProfileId,
    string Event,
    JsonElement Properties,
    DateTime OccurredAt,
    ProfileSnapshot Profile);

/// <summary>events.processed — profile attributes changed (identify or merge).</summary>
public sealed record ProfileUpdated(
    Guid TenantId,
    Guid ProfileId,
    IReadOnlyList<string> ChangedKeys,
    ProfileSnapshot Profile,
    DateTime OccurredAt);

/// <summary>events.processed — two profiles were merged; consumers must re-point cached state.</summary>
public sealed record ProfileMerged(
    Guid TenantId,
    Guid WinnerProfileId,
    Guid LoserProfileId,
    DateTime OccurredAt);
```

`src/Shared/Crm.Contracts/KafkaTopics.cs`:

```csharp
namespace Crm.Contracts;

public static class KafkaTopics
{
    public const string EventsRaw = "events.raw";
    public const string EventsProcessed = "events.processed";
    public const string EventsDlq = "events.dlq";

    public const int Partitions = 12;

    public const long RawRetentionMs = 7L * 24 * 60 * 60 * 1000;
    public const long ProcessedRetentionMs = 7L * 24 * 60 * 60 * 1000;
    public const long DlqRetentionMs = 30L * 24 * 60 * 60 * 1000;

    public const string ProfilesConsumerGroup = "profiles";
    public const string SegmentationConsumerGroup = "segmentation";
}
```

`src/Shared/Crm.Contracts/KafkaHeaders.cs`:

```csharp
namespace Crm.Contracts;

public static class KafkaHeaders
{
    public const string TenantId = "tenant-id";
    public const string MessageType = "message-type";
    public const string ContentType = "content-type";
    public const string TraceParent = "traceparent";

    public const string DlqError = "error";
    public const string DlqStack = "stack";
    public const string DlqAttempt = "attempt";
    public const string DlqSourceTopic = "source-topic";
    public const string DlqSourcePartition = "source-partition";
    public const string DlqSourceOffset = "source-offset";

    public const string JsonContentType = "application/json";
}
```

`src/Shared/Crm.Contracts/ContractJson.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crm.Contracts;

public static class ContractJson
{
    /// <summary>Wire format: camelCase, nulls omitted, case-insensitive reads.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    /// <summary>Same contract, indented — used only for human-readable snapshots.</summary>
    public static readonly JsonSerializerOptions IndentedOptions = new(Options) { WriteIndented = true };
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Crm.Contracts.UnitTests`
Expected: `Passed! - Failed: 0, Passed: 11`. If a snapshot differs only in `occurredAt` formatting (for example `2026-09-16T08:00:00Z` vs `2026-09-16T08:00:00+00:00`), the `DateTime` must be constructed with `DateTimeKind.Utc` — fix the test data, never the fixture.

- [ ] **Step 6: Commit**

```bash
git add src/Shared/Crm.Contracts tests/Crm.Contracts.UnitTests tests/fixtures/contracts
git commit -m "feat(contracts): add Kafka message records, topics, headers, and snapshot tests

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Module shells and architecture tests (AT-001…AT-006)

**Files:**
- Create: `src/Modules/Ingestion/Ingestion.Infrastructure/IngestionModule.cs`, `src/Modules/Profiles/Profiles.Infrastructure/ProfilesModule.cs`, `src/Modules/Profiles/Profiles.Infrastructure/ProfilesDbContext.cs`, `src/Modules/Segmentation/Segmentation.Infrastructure/SegmentationModule.cs`, `src/Modules/Segmentation/Segmentation.Infrastructure/SegmentationDbContext.cs`, `src/Modules/Tenancy/Tenancy.Infrastructure/TenancyModule.cs`
- Test: `tests/Crm.ArchTests/ModuleBoundaryTests.cs`, `tests/Crm.ArchTests/HostBoundaryTests.cs`, `tests/Crm.ArchTests/TenantScopingTests.cs`

**Interfaces:**
- Consumes: `TenantSaveChangesInterceptor`, `ITenantScopedDbContext`, `ModelBuilderExtensions.ApplyTenantFilters<TContext>(this ModelBuilder, TContext)`, `ITenantContext`, `IClock` (Tasks 2–3).
- Produces: `IServiceCollection AddIngestionModule(this IServiceCollection, IConfiguration)`, `AddProfilesModule`, `AddSegmentationModule`, `AddTenancyModule` (namespaces `Crm.<Module>.Infrastructure`); `ProfilesDbContext` (schema `profiles`), `SegmentationDbContext` (schema `segments`), both implementing `ITenantScopedDbContext` and calling `modelBuilder.ApplyTenantFilters(this)`. Hosts call exactly these four methods. Connection string key: `ConnectionStrings:Crm`.

- [ ] **Step 1: Write the failing architecture tests**

`tests/Crm.ArchTests/ModuleBoundaryTests.cs`:

```csharp
using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;

namespace Crm.ArchTests;

[Trait("Category", "Architecture")]
public class ModuleBoundaryTests
{
    private static readonly Dictionary<string, Assembly[]> Modules = new()
    {
        ["Ingestion"] = [typeof(Ingestion.Application.AssemblyMarker).Assembly, typeof(Ingestion.Infrastructure.AssemblyMarker).Assembly],
        ["Profiles"] = [typeof(Profiles.Domain.AssemblyMarker).Assembly, typeof(Profiles.Application.AssemblyMarker).Assembly, typeof(Profiles.Infrastructure.AssemblyMarker).Assembly],
        ["Segmentation"] = [typeof(Segmentation.Domain.AssemblyMarker).Assembly, typeof(Segmentation.Application.AssemblyMarker).Assembly, typeof(Segmentation.Infrastructure.AssemblyMarker).Assembly],
        ["Tenancy"] = [typeof(Tenancy.Domain.AssemblyMarker).Assembly, typeof(Tenancy.Application.AssemblyMarker).Assembly, typeof(Tenancy.Infrastructure.AssemblyMarker).Assembly],
    };

    public static TheoryData<string> ModuleNames => new(Modules.Keys);

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void AT001_module_depends_only_on_kernel_and_contracts(string module)
    {
        var otherModuleNamespaces = Modules.Keys.Where(m => m != module).Select(m => $"Crm.{m}").ToArray();

        foreach (var assembly in Modules[module])
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(otherModuleNamespaces).GetResult();
            result.IsSuccessful.Should().BeTrue($"{assembly.GetName().Name} must not depend on other modules: {Names(result)}");
        }
    }

    [Fact]
    public void AT002_segmentation_never_references_profiles_assemblies()
    {
        foreach (var assembly in Modules["Segmentation"])
        {
            assembly.GetReferencedAssemblies().Select(a => a.Name)
                .Should().NotContain(n => n!.StartsWith("Profiles.", StringComparison.Ordinal), $"{assembly.GetName().Name} references Profiles.*");
        }
    }

    [Fact]
    public void AT003_ingestion_has_no_database_dependency()
    {
        foreach (var assembly in Modules["Ingestion"])
        {
            assembly.GetReferencedAssemblies().Select(a => a.Name!)
                .Should().NotContain(n =>
                    n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                    || n.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal)
                    || n.StartsWith("StackExchange.Redis", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AT004_no_module_references_a_host_or_hosting()
    {
        foreach (var assembly in Modules.Values.SelectMany(a => a))
        {
            assembly.GetReferencedAssemblies().Select(a => a.Name!)
                .Should().NotContain(n => n is "Crm.Api" or "Crm.Worker" or "Crm.Hosting", $"{assembly.GetName().Name} references a host");
        }
    }

    private static string Names(TestResult result)
        => result.FailingTypes is null ? "" : string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
```

`tests/Crm.ArchTests/HostBoundaryTests.cs`:

```csharp
using System.Reflection;
using FluentAssertions;

namespace Crm.ArchTests;

[Trait("Category", "Architecture")]
public class HostBoundaryTests
{
    public static TheoryData<string, Assembly> Hosts => new()
    {
        { "Crm.Api", typeof(Crm.Api.Program).Assembly },
        { "Crm.Worker", typeof(Crm.Worker.Program).Assembly },
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void AT005_hosts_reference_only_module_infrastructure_projects(string name, Assembly host)
    {
        var moduleRefs = host.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Ingestion.", StringComparison.Ordinal)
                        || n.StartsWith("Profiles.", StringComparison.Ordinal)
                        || n.StartsWith("Segmentation.", StringComparison.Ordinal)
                        || n.StartsWith("Tenancy.", StringComparison.Ordinal))
            .ToList();

        moduleRefs.Should().NotBeEmpty($"{name} registers modules");
        moduleRefs.Should().OnlyContain(n => n.EndsWith(".Infrastructure", StringComparison.Ordinal), $"{name} may reference module Infrastructure projects only");
    }
}
```

`tests/Crm.ArchTests/TenantScopingTests.cs` (AT-006; vacuous until modules add entities, but the wiring is checked now):

```csharp
using Crm.Kernel;
using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Infrastructure;
using Crm.Segmentation.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.ArchTests;

[Trait("Category", "Architecture")]
public class TenantScopingTests
{
    private static ServiceProvider BuildProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Crm"] = "Server=localhost;Database=crm;User Id=sa;Password=x;TrustServerCertificate=True",
                ["Redis:Configuration"] = "localhost:6380",
                ["Kafka:BootstrapServers"] = "localhost:19092",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSaveChangesInterceptor>();
        services.AddProfilesModule(config);
        services.AddSegmentationModule(config);
        return services.BuildServiceProvider();
    }

    public static TheoryData<Type> DbContexts => new() { typeof(ProfilesDbContext), typeof(SegmentationDbContext) };

    [Theory]
    [MemberData(nameof(DbContexts))]
    public void AT006_every_entity_has_TenantId_and_a_query_filter(Type contextType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);

        foreach (var entityType in context.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType) && t.BaseType is null))
        {
            entityType.FindProperty(nameof(Entity.TenantId)).Should().NotBeNull($"{entityType.ClrType.Name} lacks TenantId");
            entityType.GetQueryFilter().Should().NotBeNull($"{entityType.ClrType.Name} lacks a tenant query filter");
        }
    }

    [Theory]
    [MemberData(nameof(DbContexts))]
    public void AT006_tenant_interceptor_is_registered_and_schema_is_module_owned(Type contextType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var context = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);

        var interceptors = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.Interceptors ?? [];
        interceptors.Should().Contain(i => i is TenantSaveChangesInterceptor, $"{contextType.Name} must attach TenantSaveChangesInterceptor");

        var expectedSchema = contextType == typeof(ProfilesDbContext) ? "profiles" : "segments";
        context.Model.GetDefaultSchema().Should().Be(expectedSchema);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Crm.ArchTests`
Expected: build errors — `AddProfilesModule`, `ProfilesDbContext`, `SegmentationDbContext` do not exist.

- [ ] **Step 3: Implement the module shells**

`src/Modules/Ingestion/Ingestion.Infrastructure/IngestionModule.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Ingestion.Infrastructure;

public static class IngestionModule
{
    /// <summary>Registers the Ingestion module. Producer and endpoints arrive with the Ingestion plan.</summary>
    public static IServiceCollection AddIngestionModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
```

`src/Modules/Profiles/Profiles.Infrastructure/ProfilesDbContext.cs`:

```csharp
using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Crm.Profiles.Infrastructure;

public sealed class ProfilesDbContext(DbContextOptions<ProfilesDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "profiles";

    public Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        // Entity configurations are added by the Profiles plan.
        modelBuilder.ApplyTenantFilters(this);
    }
}
```

`src/Modules/Profiles/Profiles.Infrastructure/ProfilesModule.cs`:

```csharp
using Crm.Kernel.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Profiles.Infrastructure;

public static class ProfilesModule
{
    public static IServiceCollection AddProfilesModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Crm")
            ?? throw new InvalidOperationException("ConnectionStrings:Crm is not configured.");

        services.AddDbContext<ProfilesDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory_profiles", ProfilesDbContext.Schema))
            .AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));

        return services;
    }
}
```

`src/Modules/Segmentation/Segmentation.Infrastructure/SegmentationDbContext.cs`:

```csharp
using Crm.Kernel.Data;
using Crm.Kernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Crm.Segmentation.Infrastructure;

public sealed class SegmentationDbContext(DbContextOptions<SegmentationDbContext> options, ITenantContext tenant)
    : DbContext(options), ITenantScopedDbContext
{
    public const string Schema = "segments";

    public Guid CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        // Entity configurations are added by the Segmentation plan.
        modelBuilder.ApplyTenantFilters(this);
    }
}
```

`src/Modules/Segmentation/Segmentation.Infrastructure/SegmentationModule.cs`:

```csharp
using Crm.Kernel.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Segmentation.Infrastructure;

public static class SegmentationModule
{
    public static IServiceCollection AddSegmentationModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Crm")
            ?? throw new InvalidOperationException("ConnectionStrings:Crm is not configured.");

        services.AddDbContext<SegmentationDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory_segments", SegmentationDbContext.Schema))
            .AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));

        return services;
    }
}
```

`src/Modules/Tenancy/Tenancy.Infrastructure/TenancyModule.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crm.Tenancy.Infrastructure;

public static class TenancyModule
{
    /// <summary>Registers the Tenancy module. Identity, JWT, and API keys arrive with foundation plan B.</summary>
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test Crm.sln`
Expected: ArchTests `Passed: 13` (AT001 ×4, AT002, AT003, AT004, AT005 ×2, AT006 ×4), Kernel 18, Contracts 11, other projects report no tests; zero failures, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Modules tests/Crm.ArchTests
git commit -m "feat(modules): add module registration shells, module DbContexts, and architecture tests

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Crm.Hosting — Serilog, OpenTelemetry, health checks

**Files:**
- Create: `src/Shared/Crm.Hosting/CrmHostingOptions.cs`, `Logging/SerilogExtensions.cs`, `Telemetry/OpenTelemetryExtensions.cs`, `Health/SqlServerHealthCheck.cs`, `Health/RedisHealthCheck.cs`, `Health/KafkaHealthCheck.cs`, `Health/HealthCheckExtensions.cs`, `Health/HealthResponseWriter.cs`
- Test: `tests/Crm.Api.UnitTests/Hosting/HealthResponseWriterTests.cs`

**Interfaces:**
- Consumes: nothing from modules.
- Produces (namespace `Crm.Hosting`): `builder.AddCrmSerilog(string applicationName)`, `builder.AddCrmOpenTelemetry(string serviceName)`, `services.AddCrmHealthChecks(IConfiguration)`, `app.MapCrmHealth()`, `app.MapCrmMetrics()`; configuration keys `ConnectionStrings:Crm`, `Redis:Configuration`, `Kafka:BootstrapServers`, env `OTEL_EXPORTER_OTLP_ENDPOINT`.

- [ ] **Step 1: Write the failing test for the readiness body**

`tests/Crm.Api.UnitTests/Hosting/HealthResponseWriterTests.cs`:

```csharp
using System.Text.Json;
using Crm.Hosting.Health;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crm.Api.UnitTests.Hosting;

[Trait("Category", "Unit")]
public class HealthResponseWriterTests
{
    [Fact]
    public async Task Writes_status_and_per_check_status_as_json()
    {
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["sqlserver"] = new(HealthStatus.Healthy, null, TimeSpan.Zero, null, null),
                ["redis"] = new(HealthStatus.Unhealthy, "timeout", TimeSpan.Zero, null, null),
            },
            HealthStatus.Unhealthy,
            TimeSpan.Zero);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await HealthResponseWriter.WriteAsync(context, report);

        context.Response.ContentType.Should().StartWith("application/json");
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");
        doc.RootElement.GetProperty("checks").GetProperty("sqlserver").GetString().Should().Be("Healthy");
        doc.RootElement.GetProperty("checks").GetProperty("redis").GetString().Should().Be("Unhealthy");
    }
}
```

Add to `tests/Crm.Api.UnitTests/Crm.Api.UnitTests.csproj` a `<ProjectReference Include="..\..\src\Shared\Crm.Hosting\Crm.Hosting.csproj" />`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Crm.Api.UnitTests`
Expected: build error — `HealthResponseWriter` does not exist.

- [ ] **Step 3: Implement Crm.Hosting**

`src/Shared/Crm.Hosting/CrmHostingOptions.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace Crm.Hosting;

/// <summary>Connection settings shared by every host. Bound from configuration; names match compose environment variables.</summary>
public sealed record CrmHostingOptions(string SqlConnectionString, string RedisConfiguration, string KafkaBootstrapServers)
{
    public static CrmHostingOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new CrmHostingOptions(
            Require(configuration.GetConnectionString("Crm"), "ConnectionStrings:Crm"),
            Require(configuration["Redis:Configuration"], "Redis:Configuration"),
            Require(configuration["Kafka:BootstrapServers"], "Kafka:BootstrapServers"));
    }

    private static string Require(string? value, string key)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{key} is not configured.") : value;
}
```

`src/Shared/Crm.Hosting/Logging/SerilogExtensions.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Formatting.Compact;

namespace Crm.Hosting;

public static class SerilogExtensions
{
    /// <summary>Structured JSON console logging with the application name; TenantId/TraceId are pushed via LogContext by middleware.</summary>
    public static IHostApplicationBuilder AddCrmSerilog(this IHostApplicationBuilder builder, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSerilog((services, configuration) => configuration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", applicationName)
            .WriteTo.Console(new CompactJsonFormatter()));
        return builder;
    }
}
```

`src/Shared/Crm.Hosting/Telemetry/OpenTelemetryExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Crm.Hosting;

public static class OpenTelemetryExtensions
{
    /// <summary>
    /// Traces: ASP.NET Core, HttpClient, EF Core. Metrics: ASP.NET Core, HttpClient, runtime, Prometheus scrape endpoint.
    /// OTLP export is enabled only when OTEL_EXPORTER_OTLP_ENDPOINT is set (compose observability profile sets it).
    /// </summary>
    public static IHostApplicationBuilder AddCrmOpenTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(OpenTelemetryExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health") && !ctx.Request.Path.StartsWithSegments("/metrics"))
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation()
                    .AddSource("Crm.*");
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Crm.*")
                    .AddPrometheusExporter();
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                }
            });

        return builder;
    }
}
```

`src/Shared/Crm.Hosting/Health/SqlServerHealthCheck.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crm.Hosting.Health;

public sealed class SqlServerHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 3;
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("SQL Server unreachable", ex);
        }
    }
}
```

`src/Shared/Crm.Hosting/Health/RedisHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Crm.Hosting.Health;

public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"ping {latency.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Redis unreachable", ex);
        }
    }
}
```

`src/Shared/Crm.Hosting/Health/KafkaHealthCheck.cs`:

```csharp
using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crm.Hosting.Health;

public sealed class KafkaHealthCheck(string bootstrapServers) : IHealthCheck, IDisposable
{
    private readonly IAdminClient _admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = _admin.GetMetadata(TimeSpan.FromSeconds(3));
            return Task.FromResult(metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy($"{metadata.Brokers.Count} broker(s)")
                : HealthCheckResult.Unhealthy("no brokers in metadata"));
        }
        catch (KafkaException ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka unreachable", ex));
        }
    }

    public void Dispose() => _admin.Dispose();
}
```

`src/Shared/Crm.Hosting/Health/HealthResponseWriter.cs`:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Crm.Hosting.Health;

public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Body shape: { "status": "Healthy", "checks": { "sqlserver": "Healthy", ... } }.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString(), StringComparer.Ordinal),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Options));
    }
}
```

`src/Shared/Crm.Hosting/Health/HealthCheckExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Crm.Hosting;

public static class HealthCheckExtensions
{
    public const string ReadyTag = "ready";

    /// <summary>Registers the Redis multiplexer (singleton) and the three readiness checks tagged "ready".</summary>
    public static IServiceCollection AddCrmHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        var options = CrmHostingOptions.From(configuration);
        services.AddSingleton(options);
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var redisOptions = ConfigurationOptions.Parse(options.RedisConfiguration);
            redisOptions.AbortOnConnectFail = false;
            redisOptions.ConnectTimeout = 3000;
            return ConnectionMultiplexer.Connect(redisOptions);
        });

        services.AddHealthChecks()
            .AddCheck("sqlserver", new Health.SqlServerHealthCheck(options.SqlConnectionString), HealthStatus.Unhealthy, [ReadyTag], TimeSpan.FromSeconds(3))
            .AddTypeActivatedCheck<Health.RedisHealthCheck>("redis", HealthStatus.Unhealthy, [ReadyTag], TimeSpan.FromSeconds(3))
            .AddCheck("kafka", new Health.KafkaHealthCheck(options.KafkaBootstrapServers), HealthStatus.Unhealthy, [ReadyTag], TimeSpan.FromSeconds(5));

        return services;
    }

    /// <summary>/health/live (process up) and /health/ready (all "ready" checks), both anonymous, JSON body.</summary>
    public static IEndpointRouteBuilder MapCrmHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = Health.HealthResponseWriter.WriteAsync,
        }).AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = Health.HealthResponseWriter.WriteAsync,
        }).AllowAnonymous();

        return endpoints;
    }

    public static IEndpointRouteBuilder MapCrmMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();
        return endpoints;
    }
}
```

- [ ] **Step 4: Run the test and build**

Run: `dotnet test tests/Crm.Api.UnitTests && dotnet build Crm.sln`
Expected: `Passed: 1`; build succeeds with zero warnings. If `MapPrometheusScrapingEndpoint` is not found, the `OpenTelemetry.Exporter.Prometheus.AspNetCore` beta package is missing from `Crm.Hosting.csproj` — add it (it is already listed in Task 1).

- [ ] **Step 5: Commit**

```bash
git add src/Shared/Crm.Hosting tests/Crm.Api.UnitTests
git commit -m "feat(hosting): add shared Serilog, OpenTelemetry, and health check wiring

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: Crm.Api host — Program, OpenAPI export, configuration, migrations on startup

**Files:**
- Modify: `src/Hosts/Crm.Api/Program.cs` (replace placeholder)
- Create: `src/Hosts/Crm.Api/OpenApiExport.cs`, `src/Hosts/Crm.Api/appsettings.json`, `src/Hosts/Crm.Api/appsettings.Development.json`, `src/Hosts/Crm.Api/Properties/launchSettings.json`
- Test: `tests/Crm.Api.UnitTests/OpenApiExportTests.cs`

**Interfaces:**
- Consumes: `AddCrmSerilog`, `AddCrmOpenTelemetry`, `AddCrmHealthChecks`, `MapCrmHealth`, `MapCrmMetrics` (Task 6); `Add<Module>Module` ×4, `ProfilesDbContext`, `SegmentationDbContext` (Task 5); `TenantContext`, `ITenantContext`, `IClock`, `SystemClock`, `IIdGenerator`, `UuidV7IdGenerator`, `TenantSaveChangesInterceptor` (Tasks 2–3).
- Produces: `public partial class Program` (for `WebApplicationFactory<Program>`); CLI `dotnet run --project src/Hosts/Crm.Api -- --export-openapi <path>`; route group `/v1` available to plan B via `app.MapGroup("/v1")` pattern shown below; OpenAPI document at `/openapi/v1.json`; Scalar UI at `/swagger` in Development.

- [ ] **Step 1: Write the failing test for the export argument parser**

`tests/Crm.Api.UnitTests/OpenApiExportTests.cs`:

```csharp
using Crm.Api;
using FluentAssertions;

namespace Crm.Api.UnitTests;

[Trait("Category", "Unit")]
public class OpenApiExportTests
{
    [Fact]
    public void Parses_export_path_and_strips_the_flag_from_host_args()
    {
        var (path, rest) = OpenApiExport.Parse(["--export-openapi", "out/openapi.json", "--urls", "http://x"]);

        path.Should().Be("out/openapi.json");
        rest.Should().Equal("--urls", "http://x");
    }

    [Fact]
    public void Returns_null_path_when_flag_is_absent()
    {
        var (path, rest) = OpenApiExport.Parse(["--urls", "http://x"]);

        path.Should().BeNull();
        rest.Should().Equal("--urls", "http://x");
    }

    [Fact]
    public void Flag_without_value_throws()
    {
        var act = () => OpenApiExport.Parse(["--export-openapi"]);

        act.Should().Throw<ArgumentException>().WithMessage("*--export-openapi requires a file path*");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Crm.Api.UnitTests --filter FullyQualifiedName~OpenApiExportTests`
Expected: build error — `OpenApiExport` does not exist.

- [ ] **Step 3: Implement the API host**

`src/Hosts/Crm.Api/OpenApiExport.cs`:

```csharp
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Crm.Api;

/// <summary>
/// CLI mode: `dotnet run --project src/Hosts/Crm.Api -- --export-openapi docs/api/openapi.json`
/// builds the app, writes the OpenAPI 3.x document, and exits without listening. CI diffs the result (CT-001).
/// </summary>
public static class OpenApiExport
{
    public const string Flag = "--export-openapi";

    public static (string? Path, string[] RemainingArgs) Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var index = Array.IndexOf(args, Flag);
        if (index < 0)
        {
            return (null, args);
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{Flag} requires a file path argument.", nameof(args));
        }

        var rest = args.Where((_, i) => i != index && i != index + 1).ToArray();
        return (args[index + 1], rest);
    }

    public static async Task WriteAsync(WebApplication app, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        var provider = app.Services.GetRequiredService<IOpenApiDocumentProvider>();
        var document = await provider.GetOpenApiDocumentAsync(cancellationToken: cancellationToken);

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await using var textWriter = new StreamWriter(stream);
        var writer = new OpenApiJsonWriter(textWriter);
        document.SerializeAsV3(writer);
        await textWriter.FlushAsync(cancellationToken);
        await textWriter.WriteAsync('\n');
    }
}
```

If `IOpenApiDocumentProvider.GetOpenApiDocumentAsync` has a different parameter list in 10.0.12, call it with the parameters the compiler shows (it takes an optional `HttpRequest?` and a `CancellationToken`); the returned type is `Microsoft.OpenApi.OpenApiDocument` in both cases.

`src/Hosts/Crm.Api/Program.cs`:

```csharp
using System.Diagnostics;
using Crm.Api;
using Crm.Hosting;
using Crm.Ingestion.Infrastructure;
using Crm.Kernel.Data;
using Crm.Kernel.Ids;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Infrastructure;
using Crm.Segmentation.Infrastructure;
using Crm.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;

var (exportPath, hostArgs) = OpenApiExport.Parse(args);
var builder = WebApplication.CreateBuilder(hostArgs);

builder.AddCrmSerilog("Crm.Api");
builder.AddCrmOpenTelemetry("Crm.Api");

builder.Services.AddCrmHealthChecks(builder.Configuration);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);
builder.Services.AddOpenApi("v1");

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, UuidV7IdGenerator>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<TenantSaveChangesInterceptor>();

builder.Services
    .AddIngestionModule(builder.Configuration)
    .AddProfilesModule(builder.Configuration)
    .AddSegmentationModule(builder.Configuration)
    .AddTenancyModule(builder.Configuration);

var app = builder.Build();

if (exportPath is not null)
{
    await OpenApiExport.WriteAsync(app, exportPath);
    Log.Information("OpenAPI document written to {Path}", exportPath);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();

app.MapOpenApi("/openapi/{documentName}.json");
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference("/swagger", options => options.WithOpenApiRoutePattern("/openapi/{documentName}.json"));
}

app.MapCrmHealth();
app.MapCrmMetrics();

var v1 = app.MapGroup("/v1");
_ = v1; // module endpoint groups (Tenancy, Ingestion, Profiles, Segments) attach here in later plans

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(Guid.Parse("00000000-0000-0000-0000-000000000001"), null, TenantPrincipalKind.None);
    await scope.ServiceProvider.GetRequiredService<ProfilesDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<SegmentationDbContext>().Database.MigrateAsync();
}

await app.RunAsync();

public partial class Program;
```

Why the tenant resolve before `MigrateAsync`: migrations never touch tenant rows, but the DbContext constructor takes `ITenantContext` and `ApplyTenantFilters` only reads `TenantId` at query time; the resolve is a guard so any accidental query during startup fails loudly for a fake tenant rather than throwing `TenantContextMissingException` mid-migration. `MigrateAsync` on a context with no migrations creates the database and the module's history table, which is exactly what plan A needs for readiness.

`src/Hosts/Crm.Api/appsettings.json`:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
      }
    }
  },
  "ConnectionStrings": {
    "Crm": ""
  },
  "Redis": {
    "Configuration": ""
  },
  "Kafka": {
    "BootstrapServers": ""
  },
  "AllowedHosts": "*"
}
```

`src/Hosts/Crm.Api/appsettings.Development.json` (matches `make up` ports):

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft.AspNetCore": "Information"
      }
    }
  },
  "ConnectionStrings": {
    "Crm": "Server=localhost,1433;Database=crm;User Id=sa;Password=Crm_Dev_Passw0rd!;TrustServerCertificate=True;Encrypt=False"
  },
  "Redis": {
    "Configuration": "localhost:6380"
  },
  "Kafka": {
    "BootstrapServers": "localhost:19092"
  }
}
```

`src/Hosts/Crm.Api/Properties/launchSettings.json`:

```json
{
  "profiles": {
    "Crm.Api": {
      "commandName": "Project",
      "launchBrowser": false,
      "applicationUrl": "http://localhost:8080",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

- [ ] **Step 4: Run unit tests, build, and smoke the export**

Run: `dotnet test tests/Crm.Api.UnitTests && dotnet build Crm.sln`
Expected: `Passed: 4`, clean build.

Then with the compose infrastructure running (`make up`):

```bash
dotnet run --project src/Hosts/Crm.Api -- --export-openapi /tmp/crm-openapi.json
python3 -c "import json;d=json.load(open('/tmp/crm-openapi.json'));print(d['openapi'], list(d.get('paths',{}).keys()))"
```

Expected: a version string starting with `3.` and an empty path list (no `/v1` endpoints yet; health endpoints are excluded from the document because `MapHealthChecks` does not produce API descriptions).

Then run the API and probe readiness:

```bash
dotnet run --project src/Hosts/Crm.Api &
sleep 8
curl -s http://localhost:8080/health/live
curl -s http://localhost:8080/health/ready
curl -s http://localhost:8080/metrics | head -3
kill %1
```

Expected: live `{"status":"Healthy","checks":{}}`; ready `{"status":"Healthy","checks":{"sqlserver":"Healthy","redis":"Healthy","kafka":"Healthy"}}`; metrics text starting with `# TYPE`. Verify the database was created: `sqlcmd -S localhost,1433 -U sa -P 'Crm_Dev_Passw0rd!' -C -Q "SELECT name FROM sys.databases WHERE name='crm'" -h -1` prints `crm`.

- [ ] **Step 5: Commit**

```bash
git add src/Hosts/Crm.Api tests/Crm.Api.UnitTests
git commit -m "feat(api): wire host with health, telemetry, OpenAPI export, and startup migrations

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Crm.Worker host — health on 8081 and Kafka startup probe

**Files:**
- Modify: `src/Hosts/Crm.Worker/Program.cs` (replace placeholder)
- Create: `src/Hosts/Crm.Worker/StartupProbeService.cs`, `src/Hosts/Crm.Worker/appsettings.json`, `src/Hosts/Crm.Worker/appsettings.Development.json`, `src/Hosts/Crm.Worker/Properties/launchSettings.json`
- Test: `tests/Crm.Api.UnitTests/Worker/StartupProbeServiceTests.cs` (the Api unit test project also hosts Worker unit tests to avoid a ninth tiny project; add a `<ProjectReference>` to `Crm.Worker.csproj`)

**Interfaces:**
- Consumes: Task 6 hosting extensions; Task 5 `AddProfilesModule`, `AddSegmentationModule`; Task 2–3 kernel services.
- Produces: `public partial class Program` in `Crm.Worker`; `Crm.Worker.StartupProbeService(IKafkaMetadataSource, ILogger<StartupProbeService>)`; `Crm.Worker.IKafkaMetadataSource` with `IReadOnlyList<string> GetBrokerNames(TimeSpan timeout)`; health on `http://+:8081`.

- [ ] **Step 1: Write the failing test**

`tests/Crm.Api.UnitTests/Worker/StartupProbeServiceTests.cs`:

```csharp
using Crm.Worker;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Crm.Api.UnitTests.Worker;

[Trait("Category", "Unit")]
public class StartupProbeServiceTests
{
    [Fact]
    public async Task Logs_broker_names_once_and_completes()
    {
        var source = Substitute.For<IKafkaMetadataSource>();
        source.GetBrokerNames(Arg.Any<TimeSpan>()).Returns(["kafka:9092"]);
        var logger = Substitute.For<ILogger<StartupProbeService>>();
        var service = new StartupProbeService(source, logger);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        source.Received(1).GetBrokerNames(Arg.Any<TimeSpan>());
        service.LastBrokers.Should().Equal("kafka:9092");
    }

    [Fact]
    public async Task Survives_kafka_failure_without_crashing_the_host()
    {
        var source = Substitute.For<IKafkaMetadataSource>();
        source.GetBrokerNames(Arg.Any<TimeSpan>()).Returns(_ => throw new InvalidOperationException("boom"));
        var service = new StartupProbeService(source, Substitute.For<ILogger<StartupProbeService>>());

        await service.StartAsync(CancellationToken.None);
        var act = async () => await service.ExecuteTask!;

        await act.Should().NotThrowAsync();
        service.LastBrokers.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Crm.Api.UnitTests --filter FullyQualifiedName~StartupProbeServiceTests`
Expected: build error — `Crm.Worker.StartupProbeService` does not exist.

- [ ] **Step 3: Implement**

`src/Hosts/Crm.Worker/StartupProbeService.cs`:

```csharp
using Confluent.Kafka;

namespace Crm.Worker;

public interface IKafkaMetadataSource
{
    IReadOnlyList<string> GetBrokerNames(TimeSpan timeout);
}

public sealed class AdminClientMetadataSource(string bootstrapServers) : IKafkaMetadataSource, IDisposable
{
    private readonly IAdminClient _admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

    public IReadOnlyList<string> GetBrokerNames(TimeSpan timeout)
        => _admin.GetMetadata(timeout).Brokers.Select(b => $"{b.Host}:{b.Port}").ToList();

    public void Dispose() => _admin.Dispose();
}

/// <summary>Logs Kafka broker metadata once at start-up. Consumers replace this in the Profiles plan.</summary>
public sealed class StartupProbeService(IKafkaMetadataSource metadata, ILogger<StartupProbeService> logger) : BackgroundService
{
    public IReadOnlyList<string> LastBrokers { get; private set; } = [];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            LastBrokers = metadata.GetBrokerNames(TimeSpan.FromSeconds(5));
            logger.LogInformation("Kafka reachable; brokers: {Brokers}", string.Join(", ", LastBrokers));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kafka metadata probe failed at start-up; readiness will report it");
        }

        return Task.CompletedTask;
    }
}
```

`src/Hosts/Crm.Worker/Program.cs`:

```csharp
using Crm.Hosting;
using Crm.Kernel.Data;
using Crm.Kernel.Ids;
using Crm.Kernel.Tenancy;
using Crm.Kernel.Time;
using Crm.Profiles.Infrastructure;
using Crm.Segmentation.Infrastructure;
using Crm.Worker;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddCrmSerilog("Crm.Worker");
builder.AddCrmOpenTelemetry("Crm.Worker");
builder.Services.AddCrmHealthChecks(builder.Configuration);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, UuidV7IdGenerator>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<TenantSaveChangesInterceptor>();

builder.Services
    .AddProfilesModule(builder.Configuration)
    .AddSegmentationModule(builder.Configuration);

builder.Services.AddSingleton<IKafkaMetadataSource>(sp => new AdminClientMetadataSource(sp.GetRequiredService<CrmHostingOptions>().KafkaBootstrapServers));
builder.Services.AddHostedService<StartupProbeService>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.MapCrmHealth();
app.MapCrmMetrics();

await app.RunAsync();

public partial class Program;
```

`src/Hosts/Crm.Worker/appsettings.json` — identical to the API's `appsettings.json` from Task 7 plus `"Kestrel": { "Endpoints": { "Http": { "Url": "http://+:8081" } } }` as a top-level section.

`src/Hosts/Crm.Worker/appsettings.Development.json` — identical to the API's development file from Task 7 (same localhost ports).

`src/Hosts/Crm.Worker/Properties/launchSettings.json`:

```json
{
  "profiles": {
    "Crm.Worker": {
      "commandName": "Project",
      "launchBrowser": false,
      "applicationUrl": "http://localhost:8081",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

- [ ] **Step 4: Run tests and smoke**

Run: `dotnet test tests/Crm.Api.UnitTests`
Expected: `Passed: 6`.

With `make up` running: `dotnet run --project src/Hosts/Crm.Worker & sleep 8; curl -s http://localhost:8081/health/ready; kill %1`
Expected: `{"status":"Healthy","checks":{"sqlserver":"Healthy","redis":"Healthy","kafka":"Healthy"}}` and a log line `Kafka reachable; brokers: localhost:19092`.

- [ ] **Step 5: Commit**

```bash
git add src/Hosts/Crm.Worker tests/Crm.Api.UnitTests
git commit -m "feat(worker): wire host with health on 8081 and Kafka start-up probe

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Integration test harness and readiness test (IT-041)

**Files:**
- Create: `tests/Crm.IntegrationTests/Fixtures/CrmStackFixture.cs`, `tests/Crm.IntegrationTests/Fixtures/CrmApiFactory.cs`, `tests/Crm.IntegrationTests/Fixtures/StackCollection.cs`
- Test: `tests/Crm.IntegrationTests/Ops/HealthTests.cs`

**Interfaces:**
- Consumes: `Crm.Api.Program` (Task 7).
- Produces: `CrmStackFixture` (`SqlConnectionString`, `RedisConfiguration`, `KafkaBootstrapServers`, `PauseRedisAsync()`, `UnpauseRedisAsync()`), `CrmApiFactory(CrmStackFixture)`, xUnit collection `"stack"` — plan B's integration tests reuse all three.

- [ ] **Step 1: Write the fixture**

`tests/Crm.IntegrationTests/Fixtures/CrmStackFixture.cs`:

```csharp
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Testcontainers.Kafka;
using Testcontainers.Redis;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>
/// One SQL Server (image from MSSQL_IMAGE, default SQL Server 2022), one Kafka, one Redis for the whole test run.
/// Works on Docker and on Podman (DOCKER_HOST + TESTCONTAINERS_RYUK_DISABLED=true, see docs/dev-setup.md).
/// </summary>
public sealed class CrmStackFixture : IAsyncLifetime
{
    public const string SaPassword = "Crm_Test_Passw0rd!";

    private readonly IContainer _sql = new ContainerBuilder()
        .WithImage(Environment.GetEnvironmentVariable("MSSQL_IMAGE") ?? "mcr.microsoft.com/mssql/server:2022-latest")
        .WithEnvironment("ACCEPT_EULA", "Y")
        .WithEnvironment("MSSQL_SA_PASSWORD", SaPassword)
        .WithEnvironment("MSSQL_PID", "Developer")
        .WithPortBinding(1433, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(1433))
        .Build();

    private readonly KafkaContainer _kafka = new KafkaBuilder().Build();

    private readonly RedisContainer _redis = new RedisBuilder().Build();

    public string SqlConnectionString { get; private set; } = string.Empty;

    public string RedisConfiguration { get; private set; } = string.Empty;

    public string KafkaBootstrapServers { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _kafka.StartAsync(), _redis.StartAsync());

        SqlConnectionString = $"Server={_sql.Hostname},{_sql.GetMappedPublicPort(1433)};Database=crm_test;User Id=sa;Password={SaPassword};TrustServerCertificate=True;Encrypt=False";
        RedisConfiguration = _redis.GetConnectionString();
        KafkaBootstrapServers = _kafka.GetBootstrapAddress();

        await WaitForSqlLoginAsync(TimeSpan.FromSeconds(90));
        await EnsureDatabaseAsync("crm_test");
    }

    /// <summary>The API only auto-migrates in Development; tests run as "Testing", so the fixture creates the database itself.</summary>
    private async Task EnsureDatabaseAsync(string name)
    {
        var master = new SqlConnectionStringBuilder(SqlConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}];";
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _kafka.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    public Task PauseRedisAsync() => _redis.PauseAsync();

    public Task UnpauseRedisAsync() => _redis.UnpauseAsync();

    private async Task WaitForSqlLoginAsync(TimeSpan budget)
    {
        var master = new SqlConnectionStringBuilder(SqlConnectionString) { InitialCatalog = "master" }.ConnectionString;
        var deadline = DateTime.UtcNow + budget;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var connection = new SqlConnection(master);
                await connection.OpenAsync();
                return;
            }
            catch (SqlException ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new TimeoutException($"SQL Server did not accept logins within {budget}.", last);
    }
}
```

`tests/Crm.IntegrationTests/Fixtures/StackCollection.cs`:

```csharp
namespace Crm.IntegrationTests.Fixtures;

[CollectionDefinition(Name)]
public sealed class StackCollection : ICollectionFixture<CrmStackFixture>
{
    public const string Name = "stack";
}
```

`tests/Crm.IntegrationTests/Fixtures/CrmApiFactory.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Crm.IntegrationTests.Fixtures;

/// <summary>Hosts Crm.Api in-process against the fixture's containers. Environment "Testing" so Development-only endpoints stay off unless a test opts in.</summary>
public sealed class CrmApiFactory(CrmStackFixture stack) : WebApplicationFactory<Crm.Api.Program>
{
    public string EnvironmentName { get; init; } = "Testing";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Crm"] = stack.SqlConnectionString,
            ["Redis:Configuration"] = stack.RedisConfiguration,
            ["Kafka:BootstrapServers"] = stack.KafkaBootstrapServers,
        }));
    }
}
```

- [ ] **Step 2: Write the failing readiness test**

`tests/Crm.IntegrationTests/Ops/HealthTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;

namespace Crm.IntegrationTests.Ops;

[Collection(StackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HealthTests(CrmStackFixture stack) : IAsyncLifetime
{
    private CrmApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new CrmApiFactory(stack);
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task IT041_live_is_200_and_ready_reflects_dependencies()
    {
        var live = await _client.GetAsync(new Uri("/health/live", UriKind.Relative));
        live.StatusCode.Should().Be(HttpStatusCode.OK);

        var ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        ready.StatusCode.Should().Be(HttpStatusCode.OK, await ready.Content.ReadAsStringAsync());
        var readyBody = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()).RootElement;
        readyBody.GetProperty("checks").GetProperty("sqlserver").GetString().Should().Be("Healthy");
        readyBody.GetProperty("checks").GetProperty("redis").GetString().Should().Be("Healthy");
        readyBody.GetProperty("checks").GetProperty("kafka").GetString().Should().Be("Healthy");

        await stack.PauseRedisAsync();
        try
        {
            var degraded = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            degraded.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = JsonDocument.Parse(await degraded.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("checks").GetProperty("redis").GetString().Should().Be("Unhealthy");

            var stillLive = await _client.GetAsync(new Uri("/health/live", UriKind.Relative));
            stillLive.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await stack.UnpauseRedisAsync();
        }
    }
}
```

- [ ] **Step 3: Run to verify it fails before infrastructure is right, then passes**

Run: `make test-integration`
Expected on first run: the test compiles and runs; if `PauseAsync` is not found on `RedisContainer`, upgrade is not needed — the method is on `IContainer` in Testcontainers 4.x; cast `_redis` to `IContainer` (`((IContainer)_redis).PauseAsync()`). Expected final outcome: `Passed: 1` within about 2 minutes (container pulls on first run take longer).

Podman notes for whoever runs it: `make test-integration` exports `DOCKER_HOST` from `docker context inspect` and `TESTCONTAINERS_RYUK_DISABLED=true`. `MSSQL_IMAGE=mcr.microsoft.com/azure-sql-edge:latest` must also be exported in the shell (it is in `deploy/.env`; run `set -a; source deploy/.env; set +a` in bash or `export MSSQL_IMAGE=mcr.microsoft.com/azure-sql-edge:latest` in fish before `make test-integration`). Task 12 folds this into the Makefile.

- [ ] **Step 4: Commit**

```bash
git add tests/Crm.IntegrationTests
git commit -m "test(integration): add Testcontainers stack fixture, API factory, and readiness test

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: Dockerfiles and compose services for api and worker

**Files:**
- Create: `src/Hosts/Crm.Api/Dockerfile`, `src/Hosts/Crm.Worker/Dockerfile`
- Modify: `deploy/docker-compose.yml` (add `api`, `worker` services), `deploy/.env.example` (no change needed; `AUTH_SIGNING_KEY` already present)

**Interfaces:**
- Consumes: hosts from Tasks 7–8; compose infrastructure services `sqlserver`, `kafka`, `kafka-init`, `redis` already present.
- Produces: images built from repo root context; `api` reachable at `http://localhost:8080`, `worker` health at `http://localhost:8081`.

- [ ] **Step 1: Write the Dockerfiles**

`src/Hosts/Crm.Api/Dockerfile` (build context is the repository root):

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Crm.sln ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore src/Hosts/Crm.Api/Crm.Api.csproj
RUN dotnet publish src/Hosts/Crm.Api/Crm.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD curl -fsS http://localhost:8080/health/live || exit 1
USER $APP_UID
ENTRYPOINT ["dotnet", "Crm.Api.dll"]
```

`src/Hosts/Crm.Worker/Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props Crm.sln ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore src/Hosts/Crm.Worker/Crm.Worker.csproj
RUN dotnet publish src/Hosts/Crm.Worker/Crm.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8081
EXPOSE 8081
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=6 CMD curl -fsS http://localhost:8081/health/live || exit 1
USER $APP_UID
ENTRYPOINT ["dotnet", "Crm.Worker.dll"]
```

`tests/` is copied only because `Crm.sln` references test projects and `global.json` sits beside it; `dotnet restore` of the single host project ignores them. If build time matters later, a `.dockerignore` rule can drop `tests/` and the restore keeps working because the host csproj graph never reaches tests.

- [ ] **Step 2: Add the compose services**

Append to `deploy/docker-compose.yml` under `services:` (keep existing services unchanged):

```yaml
  api:
    build:
      context: ..
      dockerfile: src/Hosts/Crm.Api/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__Crm: "Server=sqlserver,1433;Database=crm;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True;Encrypt=False"
      Redis__Configuration: "redis:6379"
      Kafka__BootstrapServers: "kafka:9092"
      Auth__SigningKey: ${AUTH_SIGNING_KEY}
      OTEL_EXPORTER_OTLP_ENDPOINT: ${OTEL_EXPORTER_OTLP_ENDPOINT:-}
    ports:
      - "8080:8080"
    depends_on:
      sqlserver:
        condition: service_healthy
      redis:
        condition: service_healthy
      kafka-init:
        condition: service_completed_successfully
    healthcheck:
      test: ["CMD", "curl", "-fsS", "http://localhost:8080/health/ready"]
      interval: 10s
      timeout: 5s
      retries: 12
      start_period: 30s

  worker:
    build:
      context: ..
      dockerfile: src/Hosts/Crm.Worker/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__Crm: "Server=sqlserver,1433;Database=crm;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True;Encrypt=False"
      Redis__Configuration: "redis:6379"
      Kafka__BootstrapServers: "kafka:9092"
      OTEL_EXPORTER_OTLP_ENDPOINT: ${OTEL_EXPORTER_OTLP_ENDPOINT:-}
    ports:
      - "8081:8081"
    depends_on:
      api:
        condition: service_healthy
    healthcheck:
      test: ["CMD", "curl", "-fsS", "http://localhost:8081/health/ready"]
      interval: 10s
      timeout: 5s
      retries: 12
      start_period: 20s
```

`worker` depends on `api` healthy so that the API's Development-only `MigrateAsync` has created database `crm` before the worker's readiness check opens a connection to it. Also update the `up` target's echo block in the `Makefile` to add:

```make
	@echo "API         http://localhost:8080  (Scalar UI at /swagger, health at /health/ready)"
	@echo "Worker      http://localhost:8081/health/ready"
```

- [ ] **Step 3: Build and run the full stack**

Run: `make down && make up`
Expected: exit 0; `make ps` shows `api` and `worker` `Up (healthy)`. Then:

```bash
curl -s http://localhost:8080/health/ready
curl -s http://localhost:8081/health/ready
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/swagger
```

Expected: both readiness bodies `Healthy` for all three checks; Scalar returns 200. If `api` fails healthcheck with a SQL login error, check `deploy/.env` has `MSSQL_SA_PASSWORD` and that `sqlserver` is healthy; the API log (`make logs`) shows the connection string host `sqlserver,1433`.

- [ ] **Step 4: Validate compose config (CT-005 locally)**

Run: `docker-compose --project-directory deploy -f deploy/docker-compose.yml config -q && echo OK`
Expected: `OK`.

- [ ] **Step 5: Commit**

```bash
git add src/Hosts/Crm.Api/Dockerfile src/Hosts/Crm.Worker/Dockerfile deploy/docker-compose.yml Makefile
git commit -m "infra: containerize api and worker and add them to compose

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: Crm.Web shell (Vite + React + TanStack + Tailwind) and compose web service

**Files:**
- Create under `src/Hosts/Crm.Web/`: `package.json`, `tsconfig.json`, `tsconfig.app.json`, `tsconfig.node.json`, `vite.config.ts`, `vitest.setup.ts`, `eslint.config.js`, `.prettierrc`, `index.html`, `playwright.config.ts`, `Dockerfile`, `nginx.conf`, `.gitignore`, `src/main.tsx`, `src/index.css`, `src/router.tsx`, `src/lib/utils.ts`, `src/lib/api.ts`, `src/components/ui/button.tsx`, `src/components/ui/button.test.tsx`, `src/routes/root.tsx`, `src/routes/home.tsx`, `src/routes/login.tsx`, `e2e/smoke.spec.ts`
- Modify: `deploy/docker-compose.yml` (add `web`), `Makefile` (`web-dev` already present; add `web-install`)

**Interfaces:**
- Consumes: API `/health/ready` (Task 7) via nginx proxy.
- Produces: `npm run dev|build|test|lint|typecheck|e2e`; `web` container on port 3000 proxying `/v1/` and `/health/` to `api:8080`; code-based TanStack router with routes `/` and `/login`; `cn()` helper; `Button` component; `apiFetch()` helper for plan B.

- [ ] **Step 1: Write the project files**

`src/Hosts/Crm.Web/package.json`:

```json
{
  "name": "crm-web",
  "private": true,
  "version": "0.1.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc -p tsconfig.app.json --noEmit && vite build",
    "preview": "vite preview",
    "typecheck": "tsc -p tsconfig.app.json --noEmit",
    "lint": "eslint .",
    "test": "vitest",
    "e2e": "playwright test"
  },
  "dependencies": {
    "@tanstack/react-query": "5.102.8",
    "@tanstack/react-router": "1.170.36",
    "class-variance-authority": "0.7.1",
    "clsx": "2.1.1",
    "lucide-react": "1.46.0",
    "react": "19.3.0",
    "react-dom": "19.3.0",
    "tailwind-merge": "3.7.0",
    "zod": "4.6.5"
  },
  "devDependencies": {
    "@eslint/js": "10.0.1",
    "@playwright/test": "1.63.0",
    "@tailwindcss/vite": "4.3.3",
    "@testing-library/react": "16.3.3",
    "@types/node": "22.20.3",
    "@types/react": "19.3.0",
    "@types/react-dom": "19.3.0",
    "@vitejs/plugin-react": "6.1.1",
    "eslint": "10.10.0",
    "eslint-plugin-react-hooks": "7.1.1",
    "globals": "17.12.0",
    "jsdom": "30.0.1",
    "prettier": "3.9.6",
    "tailwindcss": "4.3.3",
    "typescript": "5.9.3",
    "typescript-eslint": "8.70.0",
    "vite": "8.3.0",
    "vitest": "5.0.1"
  }
}
```

`src/Hosts/Crm.Web/tsconfig.json`:

```json
{
  "files": [],
  "references": [{ "path": "./tsconfig.app.json" }, { "path": "./tsconfig.node.json" }]
}
```

`src/Hosts/Crm.Web/tsconfig.app.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "jsx": "react-jsx",
    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "noFallthroughCasesInSwitch": true,
    "skipLibCheck": true,
    "isolatedModules": true,
    "resolveJsonModule": true,
    "types": ["vitest/globals"],
    "baseUrl": ".",
    "paths": { "@/*": ["src/*"] }
  },
  "include": ["src", "vitest.setup.ts"]
}
```

`src/Hosts/Crm.Web/tsconfig.node.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "skipLibCheck": true,
    "types": ["node"]
  },
  "include": ["vite.config.ts", "playwright.config.ts", "eslint.config.js"]
}
```

`src/Hosts/Crm.Web/vite.config.ts`:

```ts
/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { fileURLToPath, URL } from 'node:url';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    port: 5173,
    proxy: {
      '/v1': 'http://localhost:8080',
      '/health': 'http://localhost:8080',
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./vitest.setup.ts'],
    include: ['src/**/*.test.tsx', 'src/**/*.test.ts'],
  },
});
```

`src/Hosts/Crm.Web/vitest.setup.ts`:

```ts
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

afterEach(() => cleanup());
```

`src/Hosts/Crm.Web/eslint.config.js`:

```js
import js from '@eslint/js';
import globals from 'globals';
import reactHooks from 'eslint-plugin-react-hooks';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist', 'node_modules', 'playwright-report', 'test-results'] },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    languageOptions: { ecmaVersion: 2022, globals: globals.browser },
    plugins: { 'react-hooks': reactHooks },
    rules: { ...reactHooks.configs.recommended.rules },
  },
);
```

`src/Hosts/Crm.Web/.prettierrc`:

```json
{ "singleQuote": true, "semi": true, "printWidth": 100, "trailingComma": "all" }
```

`src/Hosts/Crm.Web/.gitignore`:

```
node_modules
dist
playwright-report
test-results
```

`src/Hosts/Crm.Web/index.html`:

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>CRM</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" src="/src/main.tsx"></script>
  </body>
</html>
```

`src/Hosts/Crm.Web/src/index.css`:

```css
@import 'tailwindcss';

:root {
  color-scheme: light dark;
}
```

`src/Hosts/Crm.Web/src/lib/utils.ts`:

```ts
import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
```

`src/Hosts/Crm.Web/src/lib/api.ts`:

```ts
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly problem: unknown,
  ) {
    super(`API request failed with ${status}`);
  }
}

/** Thin fetch wrapper: same-origin, JSON in/out, throws ApiError with the problem+json body on non-2xx. */
export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', ...(init.headers ?? {}) },
  });
  if (!response.ok) {
    const problem = await response.json().catch(() => undefined);
    throw new ApiError(response.status, problem);
  }
  return (await response.json()) as T;
}
```

`src/Hosts/Crm.Web/src/components/ui/button.tsx`:

```tsx
import * as React from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/utils';

const buttonVariants = cva(
  'inline-flex items-center justify-center rounded-md text-sm font-medium transition-colors disabled:pointer-events-none disabled:opacity-50',
  {
    variants: {
      variant: {
        default: 'bg-zinc-900 text-white hover:bg-zinc-800 dark:bg-zinc-100 dark:text-zinc-900',
        outline: 'border border-zinc-300 bg-transparent hover:bg-zinc-100 dark:border-zinc-700 dark:hover:bg-zinc-800',
        ghost: 'hover:bg-zinc-100 dark:hover:bg-zinc-800',
      },
      size: {
        default: 'h-9 px-4 py-2',
        sm: 'h-8 px-3',
        lg: 'h-10 px-6',
      },
    },
    defaultVariants: { variant: 'default', size: 'default' },
  },
);

export type ButtonProps = React.ButtonHTMLAttributes<HTMLButtonElement> & VariantProps<typeof buttonVariants>;

export function Button({ className, variant, size, ...props }: ButtonProps) {
  return <button className={cn(buttonVariants({ variant, size }), className)} {...props} />;
}
```

`src/Hosts/Crm.Web/src/components/ui/button.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { Button } from './button';

describe('Button', () => {
  it('renders children and applies variant classes', () => {
    render(<Button variant="outline">Save</Button>);
    const button = screen.getByRole('button', { name: 'Save' });
    expect(button.className).toContain('border');
  });

  it('is disabled when disabled', () => {
    render(<Button disabled>Run</Button>);
    expect((screen.getByRole('button', { name: 'Run' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
```

`src/Hosts/Crm.Web/src/routes/root.tsx`:

```tsx
import { Link, Outlet } from '@tanstack/react-router';

export function RootLayout() {
  return (
    <div className="min-h-screen bg-zinc-50 text-zinc-900 dark:bg-zinc-950 dark:text-zinc-100">
      <header className="flex items-center gap-6 border-b border-zinc-200 px-6 py-3 dark:border-zinc-800">
        <span className="font-semibold">CRM</span>
        <nav className="flex gap-4 text-sm">
          <Link to="/" className="[&.active]:font-semibold">
            Status
          </Link>
          <Link to="/login" className="[&.active]:font-semibold">
            Login
          </Link>
        </nav>
      </header>
      <main className="p-6">
        <Outlet />
      </main>
    </div>
  );
}
```

`src/Hosts/Crm.Web/src/routes/home.tsx`:

```tsx
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/lib/api';

type Readiness = { status: string; checks: Record<string, string> };

export function HomePage() {
  const { data, error, isPending } = useQuery({
    queryKey: ['health', 'ready'],
    queryFn: () => apiFetch<Readiness>('/health/ready'),
    refetchInterval: 10_000,
  });

  if (isPending) return <p>Checking API…</p>;
  if (error) return <p className="text-red-600">API unreachable: {String(error)}</p>;

  return (
    <section className="space-y-3">
      <h1 className="text-xl font-semibold">Platform status: {data.status}</h1>
      <ul className="space-y-1 text-sm">
        {Object.entries(data.checks).map(([name, status]) => (
          <li key={name} data-testid={`check-${name}`}>
            {name}: {status}
          </li>
        ))}
      </ul>
    </section>
  );
}
```

`src/Hosts/Crm.Web/src/routes/login.tsx`:

```tsx
import { Button } from '@/components/ui/button';

export function LoginPage() {
  return (
    <form className="max-w-sm space-y-4" onSubmit={(e) => e.preventDefault()}>
      <h1 className="text-xl font-semibold">Sign in</h1>
      <label className="block text-sm">
        Email
        <input name="email" type="email" className="mt-1 w-full rounded-md border px-3 py-2" />
      </label>
      <label className="block text-sm">
        Password
        <input name="password" type="password" className="mt-1 w-full rounded-md border px-3 py-2" />
      </label>
      <Button type="submit" disabled>
        Sign in (available after Tenancy lands)
      </Button>
    </form>
  );
}
```

`src/Hosts/Crm.Web/src/router.tsx`:

```tsx
import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router';
import { RootLayout } from './routes/root';
import { HomePage } from './routes/home';
import { LoginPage } from './routes/login';

const rootRoute = createRootRoute({ component: RootLayout });
const indexRoute = createRoute({ getParentRoute: () => rootRoute, path: '/', component: HomePage });
const loginRoute = createRoute({ getParentRoute: () => rootRoute, path: '/login', component: LoginPage });

export const router = createRouter({ routeTree: rootRoute.addChildren([indexRoute, loginRoute]) });

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router;
  }
}
```

`src/Hosts/Crm.Web/src/main.tsx`:

```tsx
import React from 'react';
import ReactDOM from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { router } from './router';
import './index.css';

const queryClient = new QueryClient();

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </React.StrictMode>,
);
```

`src/Hosts/Crm.Web/playwright.config.ts`:

```ts
import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  use: { baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:3000', trace: 'retain-on-failure' },
  reporter: [['list'], ['html', { open: 'never' }]],
});
```

`src/Hosts/Crm.Web/e2e/smoke.spec.ts`:

```ts
import { expect, test } from '@playwright/test';

test('status page shows all readiness checks healthy', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: /Platform status: Healthy/ })).toBeVisible();
  for (const name of ['sqlserver', 'redis', 'kafka']) {
    await expect(page.getByTestId(`check-${name}`)).toHaveText(`${name}: Healthy`);
  }
});

test('login page renders the disabled form', async ({ page }) => {
  await page.goto('/login');
  await expect(page.getByRole('button', { name: /Sign in/ })).toBeDisabled();
});
```

`src/Hosts/Crm.Web/nginx.conf`:

```nginx
server {
  listen 3000;
  server_name _;
  root /usr/share/nginx/html;
  index index.html;

  location /v1/ {
    proxy_pass http://api:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
  }

  location /health/ {
    proxy_pass http://api:8080;
  }

  location / {
    try_files $uri /index.html;
  }
}
```

`src/Hosts/Crm.Web/Dockerfile` (build context is `src/Hosts/Crm.Web`):

```dockerfile
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:1.29-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 3000
HEALTHCHECK --interval=10s --timeout=3s --retries=6 CMD wget -qO- http://localhost:3000/ > /dev/null || exit 1
```

- [ ] **Step 2: Install, run the unit test, lint, typecheck**

```bash
cd src/Hosts/Crm.Web
npm install
npm test -- --run
npm run lint
npm run typecheck
npm run build
```

Expected: `npm install` writes `package-lock.json` (commit it); vitest `2 passed`; lint clean; typecheck clean; `dist/` produced. If `tsc` complains about `import.meta.url` in `vite.config.ts`, ensure `tsconfig.node.json` includes `"module": "ESNext"` (it does) and that the file is not included by `tsconfig.app.json`.

- [ ] **Step 3: Add the compose web service and run the e2e smoke**

Append to `deploy/docker-compose.yml` under `services:`:

```yaml
  web:
    build:
      context: ../src/Hosts/Crm.Web
      dockerfile: Dockerfile
    ports:
      - "3000:3000"
    depends_on:
      api:
        condition: service_healthy
```

Add to the `Makefile`:

```make
web-install: ## install web dependencies
	cd src/Hosts/Crm.Web && npm ci

e2e: ## run Playwright against the compose stack (make up first)
	cd src/Hosts/Crm.Web && npx playwright install --with-deps chromium && npm run e2e
```

and extend the `up` echo block with `@echo "Web         http://localhost:3000"`.

Run: `make up && make e2e`
Expected: `web` healthy; Playwright `2 passed`.

- [ ] **Step 4: Commit**

```bash
git add src/Hosts/Crm.Web deploy/docker-compose.yml Makefile
git commit -m "feat(web): add React shell with router, query, tailwind, tests, and nginx image

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: CI workflow, Makefile completion, developer setup doc, README status

**Files:**
- Create: `.github/workflows/ci.yml`, `docs/dev-setup.md`
- Modify: `Makefile` (`test-integration` exports `MSSQL_IMAGE` from `deploy/.env`), `README.md` (status table, quickstart, badge), `tools/docs_check.py` (manifest entry for `docs/dev-setup.md`), `docs/README.md` (link)

**Interfaces:**
- Consumes: everything above.
- Produces: green CI on `main`; `make test`, `make test-unit`, `make test-integration`, `make docs-check`, `make openapi` all functional.

- [ ] **Step 1: Finish the Makefile**

Replace the `test-integration` target with:

```make
test-integration: env ## run Testcontainers-based tests against the local engine
	@set -a; . ./$(ENV_FILE); set +a; \
	DOCKER_HOST=$$(docker context inspect --format '{{.Endpoints.docker.Host}}') \
	TESTCONTAINERS_RYUK_DISABLED=true \
	MSSQL_IMAGE=$$MSSQL_IMAGE \
	dotnet test Crm.sln --filter "Category=Integration" --logger "console;verbosity=normal"
```

Remove the `(plan A)` / `(plan B)` suffixes from the `test-unit`, `test-integration`, `test`, `openapi`, `web-dev` help strings; leave `migrate` and `seed` marked `(plan B)`.

- [ ] **Step 2: Write the CI workflow**

`.github/workflows/ci.yml`:

```yaml
name: ci

on:
  push:
    branches: [main]
  pull_request:

env:
  DOTNET_NOLOGO: "1"
  DOTNET_CLI_TELEMETRY_OPTOUT: "1"
  CI: "true"

jobs:
  dotnet:
    name: build + unit/arch/contract tests
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('Directory.Packages.props') }}
      - run: dotnet restore Crm.sln
      - run: dotnet build Crm.sln --no-restore -c Release -warnaserror
      - run: dotnet test Crm.sln --no-build -c Release --filter "Category!=Integration" --logger trx --results-directory TestResults
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: unit-test-results
          path: TestResults

  integration:
    name: integration tests (Testcontainers)
    runs-on: ubuntu-latest
    timeout-minutes: 25
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - run: dotnet restore Crm.sln
      - run: dotnet test Crm.sln --no-restore --filter "Category=Integration" --logger trx --results-directory TestResults
        env:
          MSSQL_IMAGE: mcr.microsoft.com/mssql/server:2022-latest
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: integration-test-results
          path: TestResults

  contracts:
    name: docs check, OpenAPI drift, compose config
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - run: python3 tools/docs_check.py
      - name: export OpenAPI and diff against committed document
        run: |
          mkdir -p build
          dotnet run --project src/Hosts/Crm.Api -c Release -- --export-openapi build/openapi.json
          if [ -f docs/api/openapi.json ]; then
            diff -u docs/api/openapi.json build/openapi.json
          else
            echo "docs/api/openapi.json not committed yet (plan B); export succeeded"
          fi
        env:
          ConnectionStrings__Crm: "Server=localhost;Database=crm;User Id=sa;Password=unused;TrustServerCertificate=True"
          Redis__Configuration: "localhost:6379"
          Kafka__BootstrapServers: "localhost:9092"
      - name: validate compose files
        run: |
          cp deploy/.env.example deploy/.env
          docker compose --project-directory deploy -f deploy/docker-compose.yml config -q

  web:
    name: web lint, typecheck, unit tests
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: src/Hosts/Crm.Web
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: 22
          cache: npm
          cache-dependency-path: src/Hosts/Crm.Web/package-lock.json
      - run: npm ci
      - run: npm run lint
      - run: npm run typecheck
      - run: npm test -- --run
```

The OpenAPI export step needs the three configuration values only because `AddCrmHealthChecks` validates them at startup; the export never connects. The compose config step uses the Docker CLI plugin available on the GitHub runner (`docker compose`), whereas the local Makefile uses the standalone `docker-compose` binary — same file format.

- [ ] **Step 3: Write the developer setup document and register it with the checker**

`docs/dev-setup.md`:

```markdown
# Developer Setup

How to run the CRM platform locally. Verified on macOS (Apple Silicon) with Podman; the same commands work with Docker Desktop.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.400 (pinned in `global.json`) | `dotnet --version` |
| Node | 22 LTS or newer | 26 works locally; the web image builds with 22 |
| Container engine | Podman 6.x machine or Docker Desktop | `docker context ls` shows which is active |
| docker-compose | standalone v2/v5 binary, or the `docker compose` plugin | the Makefile calls `docker-compose` |
| sqlcmd | optional | for ad-hoc queries against `localhost,1433` |
| k6 | optional (`brew install k6`) | load tests, later plan |

## First run

```bash
make up          # copies deploy/.env.example to deploy/.env on first run, starts everything
make ps          # all services Up (healthy)
open http://localhost:3000        # web shell
open http://localhost:8080/swagger # API reference (Development only)
```

## Podman specifics

- The SQL Server 2022 image is amd64-only and segfaults under QEMU emulation on the libkrun Podman machine (no Rosetta). `deploy/.env` therefore sets `MSSQL_IMAGE=mcr.microsoft.com/azure-sql-edge:latest`, an arm64-native engine with the same T-SQL/JSON surface slice 1 uses. CI runs SQL Server 2022; if a query works on Edge but fails in CI, CI wins.
- Redis is published on host port 6380 (`REDIS_HOST_PORT`) because 6379 is frequently taken by other local projects.
- Testcontainers needs the Podman socket and no Ryuk: `make test-integration` exports `DOCKER_HOST` from `docker context inspect`, `TESTCONTAINERS_RYUK_DISABLED=true`, and `MSSQL_IMAGE` from `deploy/.env`. Run it directly with those three variables set if you bypass the Makefile.
- Port conflicts show up as `proxy already running` from the Podman API. Check `docker ps -a --format '{{.Names}} {{.Ports}}'` for the container holding the port and change the host port in `deploy/.env`.

## Everyday commands

| Command | Purpose |
|---|---|
| `make test-unit` | unit, architecture, contract tests (seconds, no containers) |
| `make test-integration` | Testcontainers suite (minutes; first run pulls images) |
| `make test` | both |
| `make docs-check` | documentation checker |
| `make openapi` | regenerate `docs/api/openapi.json` (commit the result) |
| `make web-dev` | Vite dev server on 5173 proxying `/v1` and `/health` to `localhost:8080` |
| `make e2e` | Playwright against the compose stack |
| `make logs` | follow api and worker logs |
| `make down` | stop and delete volumes (`KEEP=1 make down` keeps data) |

## Running hosts from the IDE

`dotnet run --project src/Hosts/Crm.Api` and `dotnet run --project src/Hosts/Crm.Worker` use `appsettings.Development.json`, which points at the compose ports (`localhost,1433`, `localhost:6380`, `localhost:19092`). Stop the compose `api`/`worker` containers first to free ports 8080/8081, or run with `--urls http://localhost:8090`.
```

Add to `tools/docs_check.py` `REQUIRED`:

```python
    "docs/dev-setup.md": ["# Developer Setup", "## Prerequisites", "## First run", "## Podman specifics", "## Everyday commands"],
```

Add a row to `docs/README.md` Documents table: `| [Developer Setup](dev-setup.md) | Prerequisites, first run, Podman specifics, everyday commands |`.

- [ ] **Step 4: Update the root README**

In `README.md`:
- Add under the title: `![ci](https://github.com/fef-ai-ml/crm/actions/workflows/ci.yml/badge.svg)`.
- Status table: change `Implementation: Kernel, Contracts, Tenancy | Planned` to two rows — `Implementation: solution skeleton, Kernel, Contracts, hosts, web shell, CI | Complete` and `Implementation: Tenancy | Planned`.
- Quickstart: replace the three commands with:

```bash
make up                         # infrastructure + api + worker + web, waits until healthy
open http://localhost:3000      # web shell shows readiness of SQL, Kafka, Redis
open http://localhost:8080/swagger
```

- Add a "Developer setup" line linking `docs/dev-setup.md` to the Documentation table.

- [ ] **Step 5: Run everything, then verify CI on the pushed commit**

```bash
python3 tools/docs_check.py
make test-unit
make test-integration
make down && make up && make e2e
```

Expected: docs check `0 errors` (manifest now 30 documents); unit run reports Kernel 18, Contracts 11, Api unit 6, Arch 13, zero failures; integration `Passed: 1`; e2e `2 passed`.

- [ ] **Step 6: Commit and push, then watch CI**

```bash
git add .github Makefile docs/dev-setup.md docs/README.md README.md tools/docs_check.py
git commit -m "ci: add GitHub Actions pipeline, developer setup guide, and README status

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git push origin main
gh run watch --exit-status
```

Expected: all four jobs green. If the `integration` job fails on container start-up, re-run once (runner image pulls can time out); a second failure is a real defect to fix, not to skip.

---

## Test cases turned green by this plan

| Case | Task |
|---|---|
| UT-054, UT-055, UT-057 | 2, 3 |
| AT-001, AT-002, AT-003, AT-004, AT-005, AT-006 | 5 |
| CT-003 (docs check), CT-004 (contract snapshots), CT-005 (compose config) | 12, 4, 10/12 |
| IT-041 | 9 |

Pending after this plan (owned by plan B and later): UT-001…UT-053, UT-056, UT-058, CT-001, CT-002, IT-001…IT-040, IT-042, E2E-001…E2E-008, LT-001…LT-005.

## Self-review notes

- **Spec coverage:** §3 → Task 1; §4 → Tasks 2–3; §5 → Task 4; §7.1 → Task 7 (Tenancy endpoints, problem mapper for `Result`, rate limiter are plan B); §7.2 → Task 8; §7.3 → Task 11 (shadcn/ui is represented by the cva/clsx/tailwind-merge `Button` pattern and `components/ui/` layout; the shadcn CLI is not run because it is interactive); §8.1 → Task 10 + existing compose; §8.2 observability compose file → plan C; §8.3 Makefile → Tasks 10–12 (`migrate`, `seed` → plan B); §9 → Task 12; §2 environment facts → `docs/dev-setup.md`.
- **Deviation from spec §2.3:** new shared project `Crm.Hosting` for host cross-cutting wiring; modules are forbidden from referencing it (AT-004 extended).
- **Type consistency:** `TenantContext.Resolve(Guid, Guid?, TenantPrincipalKind)`, `TenantSaveChangesInterceptor(ITenantContext, IClock)`, `ApplyTenantFilters(ITenantContext)`, `Add<Module>Module(IServiceCollection, IConfiguration)`, `CrmHostingOptions.From(IConfiguration)`, `OpenApiExport.Parse/WriteAsync`, `IKafkaMetadataSource.GetBrokerNames(TimeSpan)`, `CrmStackFixture.PauseRedisAsync/UnpauseRedisAsync`, `CrmApiFactory(CrmStackFixture)` are used with the same names and arities in every task that references them.


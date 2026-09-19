# Foundation C — Developer Experience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the foundation: an opt-in observability stack (Prometheus, Grafana, Jaeger) wired to the API and Worker, the minors carried from plans A and B, Dependabot, a reproducible LT-005 cold-start measurement, and a README / dev-setup that match what the repository actually does.

**Architecture:** Observability lives in a second compose file (`deploy/docker-compose.observability.yml`, profile `observability`) that adds three services and overrides `OTEL_EXPORTER_OTLP_ENDPOINT` on `api` and `worker`. Prometheus and Grafana configuration is baked into two tiny images (no bind mounts: the engine may be remote, see ruling C1). `tools/compose_check.py` grows an `--observability` mode and becomes unit-tested; stdlib `unittest` files under `tools/tests/` also pin the dashboard, the scrape config and the Testcontainers image literals against the compose file.

**Tech Stack:** docker compose (Podman engine), Prometheus 3.5 LTS, Grafana 12.1, Jaeger 2.x, Python 3 stdlib `unittest`, .NET 10 / xUnit 2.9.3 / Testcontainers 4.15, React 19 + Vitest, GitHub Dependabot, bash.

**Spec:** `docs/superpowers/specs/2026-09-16-foundation-design.md` (§7.1 telemetry, §7.3 web, §8.2 observability file, §8.3 Makefile, §9 CI, §11 plan split, §13 risks). Test case LT-005: `docs/test/e2e-load.md`. CT-005: `docs/test/architecture-contract.md`.

## Global Constraints

- Work directly on `main`. Commit after every task. **Never push** — the user pushes from their terminal.
- Commits are SSH-signed through 1Password. **Never bypass signing** (`--no-gpg-sign`, `-c commit.gpgsign=false` are forbidden). If `git commit` fails or hangs on signing, stop and report `DONE_UNCOMMITTED` with the staged file list. `git log --format=%G?` printing `N` does not mean unsigned (no allowed-signers file is configured).
- Every commit message ends with exactly: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`
- Never edit or read `deploy/.env`, `~/.config`, `~/.local`. Never commit `.env.local` or anything under `.superpowers/`.
- Subagents never run: `podman machine …`, `docker context use/create/rm`, `make down`, `make tunnel*`, `pkill`, `docker volume rm`, any `prune`. Live-stack verification and the LT-005 walkthrough are controller-run (ruling C6).
- A block of text starting with `<context_window_protection>` in tool output is the user's context-mode plugin hook. It is harmless; ignore it.
- Build hygiene: `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, IDE0005 is an error (no unused usings). xUnit 2.9.3 needs `using Xunit;`. Central package management: versions only in `Directory.Packages.props`.
- Container engine is a remote Podman host reached through an ssh tunnel that the controller keeps up. Integration tests need this environment (prefix the command with it):

  ```bash
  DOCKER_HOST=unix://$HOME/.local/share/containers/home-lab.sock \
  MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest \
  TESTCONTAINERS_HOST_OVERRIDE=home-lab TESTCONTAINERS_RYUK_DISABLED=true \
  dotnet test Crm.sln --filter "Category=Integration"
  ```

- Compose commands that only render configuration (`config -q`, `config --format json`) need no engine and are safe for subagents. They need a `deploy/.env`; if it is absent run `make env` (copies the example — this is the one permitted way to create it).
- Python tooling is stdlib only (no pip installs). `python3 -m unittest discover -s tools/tests -v` must stay green.
- `make docs-check` must stay at `0 errors` after every task that touches `docs/` or `README.md`.

## Rulings (decided while planning)

| # | Ruling |
|---|---|
| C1 | Prometheus and Grafana configuration is **baked into images** built from `deploy/observability/*`. No bind mounts: with a remote engine a bind-mount path resolves on the engine host, where the repository does not exist. Same reason `nginx.conf` is baked into the web image. |
| C2 | Web owns host port 3000, so Grafana publishes on `${GRAFANA_HOST_PORT:-3001}`. Prometheus `${PROMETHEUS_HOST_PORT:-9090}`, Jaeger UI `${JAEGER_HOST_PORT:-16686}`. OTLP 4317 is not published (compose network only). |
| C3 | LT-005 is recorded at **foundation scope**: `make up` ≤ 180 s, `make seed` ≤ 120 s, quickstart probe = login + `GET /v1/api-keys` → 200. The `202` from `/v1/track` and the 5k-profile / 100k-event seed volume do not exist yet; the Ingestion plan switches the probe to `/v1/track`, the Profiles plan adds the seed volume. LT-005 stays listed as pending-in-full. |
| C4 | Overrides go on the make command line (`make up MSSQL_HOST_PORT=11433`), not in the environment: the Makefile `-include`s `deploy/.env`, and a variable assigned in an included file beats (and re-exports over) an environment variable; only a command-line variable wins. |
| C5 | Jaeger v1 (`all-in-one`) is end-of-life; use the v2 image `jaegertracing/jaeger`. It receives OTLP gRPC on 4317 and serves the UI on 16686 with no configuration. |
| C6 | Anything that starts, stops or re-tunnels the live stack is run by the controller, not by a subagent. Subagents verify with rendered compose config, unit tests, and the Testcontainers suite. |
| C7 | `make down`, `make ps` always pass both compose files and the profile, so observability containers are never orphaned by a plain `make down`. `make up` keeps using only the base file. |
| C8 | Testcontainers images are aligned with compose (`apache/kafka:3.9.1`, `redis:7-alpine`) and pinned by a unit test that reads both files. SQL Server stays `MSSQL_IMAGE`-driven. |
| C9 | LT-005 measured from the working checkout with cached image layers, not a fresh clone: a fresh clone rebuilds the restore layer and would measure NuGet download time, which LT-005 does not budget. Observability ports bind to the engine's loopback (final review I3). |

## Test cases in scope

| Case | State after this plan |
|---|---|
| CT-005 | extended: observability compose file validated by `compose_check.py --observability` in CI |
| LT-005 | foundation scope measured and recorded (ruling C3); remains pending in full |
| UT-057, CT-004 | unchanged behaviour; test methods renamed to carry their case ids |
| IT-040 | still pending (pipeline-level); this plan adds an un-numbered `/metrics` characterization test |

All other pending cases stay pending.

## File structure

| Path | Responsibility |
|---|---|
| `tests/Crm.IntegrationTests/Ops/MetricsTests.cs` (new) | `/metrics` is anonymous Prometheus text with HTTP server metrics — what the scrape config relies on |
| `tools/compose_check.py` (modify) | pure `check(config, observability)` + CLI wrapper |
| `tools/tests/test_compose_check.py` (new) | unit tests for both modes |
| `tools/tests/test_observability_assets.py` (new) | dashboard JSON, datasource, scrape config invariants |
| `tools/tests/test_image_pins.py` (new) | Testcontainers image literals exist in the compose file |
| `deploy/observability/prometheus/{Dockerfile,prometheus.yml}` (new) | scrape config image |
| `deploy/observability/grafana/{Dockerfile,provisioning/**,dashboards/crm-overview.json}` (new) | provisioned Grafana image |
| `deploy/docker-compose.observability.yml` (new) | the three services + OTLP endpoint override |
| `deploy/.env.example`, `Makefile`, `.github/workflows/ci.yml` (modify) | ports, targets, CI validation |
| `src/Hosts/Crm.Web/src/components/ui/{input,card}.tsx` + tests (new) | shadcn-style primitives the spec promised |
| `src/Hosts/Crm.Web/src/routes/{login,home}.tsx`, `package.json` (modify) | use the primitives; typecheck the node tsconfig |
| `.github/dependabot.yml` (new) | dependency update policy |
| `tests/Crm.LoadTests/coldstart.sh` (new) | LT-005 measurement script |
| `README.md`, `docs/dev-setup.md`, spec, `docs/test/*.md`, `docs/data-dictionary/redis-and-kafka.md` (modify) | final docs |

---

### Task 1: `/metrics` characterization test

The Prometheus scrape config (Task 3) depends on `/metrics` being anonymous and exposing `http_server_request_duration_seconds`. Pin that first. The endpoint already exists (`MapCrmMetrics` in `src/Shared/Crm.Hosting/Health/HealthCheckExtensions.cs`), so this test is expected to pass on first run; a failure is a finding to report, not something to paper over.

**Files:**
- Create: `tests/Crm.IntegrationTests/Ops/MetricsTests.cs`

**Interfaces:**
- Consumes: `CrmStackFixture`, `CrmApiFactory(CrmStackFixture)`, `StackCollectionDefinition.Name` (all in `tests/Crm.IntegrationTests/Fixtures`), same shape as `Ops/HealthTests.cs`.
- Produces: nothing other tasks import.

- [ ] **Step 1: Write the test**

```csharp
using System.Diagnostics;
using System.Net;
using Crm.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace Crm.IntegrationTests.Ops;

[Collection(StackCollectionDefinition.Name)]
[Trait("Category", "Integration")]
public sealed class MetricsTests(CrmStackFixture stack) : IAsyncLifetime
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
    public async Task Metrics_endpoint_is_anonymous_prometheus_text_with_http_server_metrics()
    {
        // One served request so the ASP.NET Core meter has a measurement to export.
        (await _client.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.OK);

        // The duration is recorded when the server finishes the request, which can trail the client
        // seeing the response, and the exporter caches a scrape briefly; poll instead of asserting once.
        var deadline = Stopwatch.StartNew();
        string body;
        HttpResponseMessage response;
        do
        {
            response = await _client.GetAsync(new Uri("/metrics", UriKind.Relative));
            body = await response.Content.ReadAsStringAsync();
            if (body.Contains("http_server_request_duration_seconds", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        while (deadline.Elapsed < TimeSpan.FromSeconds(10));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "no credentials were sent: /metrics is anonymous for the Prometheus scraper");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        body.Should().Contain("http_server_request_duration_seconds", "the Grafana HTTP panels query this metric");
    }
}
```

- [ ] **Step 2: Run it**

Run (with the Global Constraints environment prefix): `dotnet test tests/Crm.IntegrationTests --filter "FullyQualifiedName~MetricsTests"`
Expected: PASS (1 test). If it fails, stop and report the body/headers observed — do not change `src/`.

- [ ] **Step 3: Build hygiene**

Run: `dotnet build Crm.sln -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add tests/Crm.IntegrationTests/Ops/MetricsTests.cs
git commit -m "test(integration): pin /metrics as anonymous Prometheus text with HTTP server metrics

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `compose_check.py` — testable core and `--observability` mode

**Files:**
- Modify: `tools/compose_check.py` (whole file replaced below)
- Create: `tools/tests/test_compose_check.py`
- Modify: `.github/workflows/ci.yml` (contracts job: add the unittest step)

**Interfaces:**
- Produces: `compose_check.check(config: dict, observability: bool = False) -> list[str]` (empty list = valid); CLI `python3 tools/compose_check.py [--observability]` reading rendered JSON on stdin, exit 0/1/2 as today. Task 3 relies on the observability expectations: services `prometheus`, `grafana`, `jaeger`; container ports 9090 / 3000 / 16686 published; healthchecks on `prometheus` and `grafana`; `api` and `worker` environment `OTEL_EXPORTER_OTLP_ENDPOINT == "http://jaeger:4317"`.

- [ ] **Step 1: Write the failing tests**

`tools/tests/test_compose_check.py`:

```python
"""Unit tests for tools/compose_check.py (CT-005). Run: python3 -m unittest discover -s tools/tests -v"""
from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import compose_check  # noqa: E402


def svc(target: int | None = None, published: str | None = None, healthcheck: bool = False, **extra) -> dict:
    service: dict = dict(extra)
    if target is not None:
        service["ports"] = [{"target": target, "published": published or str(target)}]
    if healthcheck:
        service["healthcheck"] = {"test": ["CMD", "true"]}
    return service


def base_config() -> dict:
    infra_deps = {
        "sqlserver": {"condition": "service_healthy"},
        "redis": {"condition": "service_healthy"},
        "kafka-init": {"condition": "service_completed_successfully"},
    }
    return {
        "services": {
            "sqlserver": svc(1433, "11433", healthcheck=True),
            "kafka": svc(19092, healthcheck=True),
            "kafka-init": svc(),
            "redis": svc(6379, "6380", healthcheck=True),
            "api": svc(8080, healthcheck=True, environment={"OTEL_EXPORTER_OTLP_ENDPOINT": ""}),
            "worker": svc(8081, healthcheck=True, depends_on=infra_deps, environment={"OTEL_EXPORTER_OTLP_ENDPOINT": ""}),
            "web": svc(3000),
        }
    }


def observability_config() -> dict:
    config = base_config()
    services = config["services"]
    services["prometheus"] = svc(9090, healthcheck=True)
    services["grafana"] = svc(3000, "3001", healthcheck=True)
    services["jaeger"] = svc(16686)
    for name in ("api", "worker"):
        services[name]["environment"]["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://jaeger:4317"
    return config


class BaseModeTests(unittest.TestCase):
    def test_valid_base_config_has_no_errors(self):
        self.assertEqual(compose_check.check(base_config()), [])

    def test_unexpected_service_is_reported(self):
        config = base_config()
        config["services"]["adminer"] = svc(8888)
        errors = compose_check.check(config)
        self.assertTrue(any("unexpected services" in e and "adminer" in e for e in errors), errors)

    def test_observability_services_are_unexpected_in_base_mode(self):
        errors = compose_check.check(observability_config())
        self.assertTrue(any("unexpected services" in e and "jaeger" in e for e in errors), errors)

    def test_wrong_published_port_is_reported(self):
        config = base_config()
        config["services"]["api"]["ports"][0]["published"] = "9999"
        errors = compose_check.check(config)
        self.assertTrue(any(e.startswith("api:") and "8080" in e for e in errors), errors)

    def test_missing_worker_dependency_is_reported(self):
        config = base_config()
        del config["services"]["worker"]["depends_on"]["redis"]
        errors = compose_check.check(config)
        self.assertTrue(any("worker: missing depends_on entry for redis" in e for e in errors), errors)


class ObservabilityModeTests(unittest.TestCase):
    def test_valid_observability_config_has_no_errors(self):
        self.assertEqual(compose_check.check(observability_config(), observability=True), [])

    def test_base_config_is_missing_the_observability_services(self):
        errors = compose_check.check(base_config(), observability=True)
        self.assertTrue(any("missing services" in e and "prometheus" in e for e in errors), errors)

    def test_grafana_without_healthcheck_is_reported(self):
        config = observability_config()
        del config["services"]["grafana"]["healthcheck"]
        errors = compose_check.check(config, observability=True)
        self.assertIn("grafana: no healthcheck defined", errors)

    def test_api_without_the_otlp_endpoint_is_reported(self):
        config = observability_config()
        config["services"]["api"]["environment"]["OTEL_EXPORTER_OTLP_ENDPOINT"] = ""
        errors = compose_check.check(config, observability=True)
        self.assertTrue(any(e.startswith("api:") and "OTEL_EXPORTER_OTLP_ENDPOINT" in e for e in errors), errors)

    def test_environment_in_list_form_is_understood(self):
        config = observability_config()
        config["services"]["worker"]["environment"] = ["OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317"]
        self.assertEqual(compose_check.check(config, observability=True), [])

    def test_check_does_not_mutate_its_input(self):
        config = observability_config()
        snapshot = copy.deepcopy(config)
        compose_check.check(config, observability=True)
        self.assertEqual(config, snapshot)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 -m unittest discover -s tools/tests -v`
Expected: errors — `module 'compose_check' has no attribute 'check'`.

- [ ] **Step 3: Replace `tools/compose_check.py`**

```python
#!/usr/bin/env python3
"""Validate the rendered compose configuration against CT-005.

Usage:
  <compose> -f deploy/docker-compose.yml config --format json | python3 tools/compose_check.py
  <compose> -f deploy/docker-compose.yml -f deploy/docker-compose.observability.yml \
      --profile observability config --format json | python3 tools/compose_check.py --observability

Asserts (see docs/test/architecture-contract.md CT-005):
- the service set is exactly {sqlserver, kafka, kafka-init, redis, api, worker, web}
  (plus {prometheus, grafana, jaeger} with --observability)
- published ports: 8080->api, 8081->worker, 3000->web, 19092->kafka, and a published port mapped to
  container port 1433 on sqlserver / 6379 on redis (host ports for those are configurable via
  deploy/.env, so only the container-side target is fixed). With --observability: container ports
  9090 (prometheus), 3000 (grafana), 16686 (jaeger) are published, host side configurable
- sqlserver, kafka, redis, api and worker each declare a healthcheck (web's readiness comes from its
  image, so either is accepted); with --observability also prometheus and grafana (the jaeger image
  ships no shell to run one)
- worker's depends_on matches the infrastructure-only wiring: sqlserver and redis healthy,
  kafka-init completed successfully
- with --observability: api and worker export OTLP to http://jaeger:4317

Exits non-zero with a message naming every mismatch found.
"""
from __future__ import annotations

import json
import sys

BASE_SERVICES = {"sqlserver", "kafka", "kafka-init", "redis", "api", "worker", "web"}
OBSERVABILITY_SERVICES = {"prometheus", "grafana", "jaeger"}

BASE_HEALTHCHECKS = {"sqlserver", "kafka", "redis", "api", "worker"}
OBSERVABILITY_HEALTHCHECKS = {"prometheus", "grafana"}

# service -> (container target port, expected published port or None to accept any)
BASE_PORTS: dict[str, tuple[int, str | None]] = {
    "api": (8080, "8080"),
    "worker": (8081, "8081"),
    "web": (3000, "3000"),
    "kafka": (19092, "19092"),
    "sqlserver": (1433, None),
    "redis": (6379, None),
}
OBSERVABILITY_PORTS: dict[str, tuple[int, str | None]] = {
    "prometheus": (9090, None),
    "grafana": (3000, None),
    "jaeger": (16686, None),
}

WORKER_DEPENDS_ON = {
    "sqlserver": "service_healthy",
    "redis": "service_healthy",
    "kafka-init": "service_completed_successfully",
}

OTLP_VARIABLE = "OTEL_EXPORTER_OTLP_ENDPOINT"
OTLP_ENDPOINT = "http://jaeger:4317"
OTLP_SERVICES = ("api", "worker")


def find_port(service: dict, target: int) -> dict | None:
    for port in service.get("ports", []):
        try:
            if int(port.get("target")) == target:
                return port
        except (TypeError, ValueError):
            continue
    return None


def environment_of(service: dict) -> dict[str, str]:
    """Compose renders `environment` as a mapping; tolerate the KEY=value list form as well."""
    environment = service.get("environment") or {}
    if isinstance(environment, dict):
        return {str(key): "" if value is None else str(value) for key, value in environment.items()}
    result: dict[str, str] = {}
    for entry in environment:
        key, _, value = str(entry).partition("=")
        result[key] = value
    return result


def check(config: dict, observability: bool = False) -> list[str]:
    """Return every CT-005 mismatch in a rendered compose config; an empty list means valid."""
    expected_services = BASE_SERVICES | (OBSERVABILITY_SERVICES if observability else set())
    expected_ports = {**BASE_PORTS, **(OBSERVABILITY_PORTS if observability else {})}
    healthcheck_required = BASE_HEALTHCHECKS | (OBSERVABILITY_HEALTHCHECKS if observability else set())

    errors: list[str] = []
    services = config.get("services", {})
    actual_names = set(services.keys())

    if actual_names != expected_services:
        missing = expected_services - actual_names
        extra = actual_names - expected_services
        if missing:
            errors.append(f"missing services: {sorted(missing)}")
        if extra:
            errors.append(f"unexpected services: {sorted(extra)}")

    for name, (target, expected_published) in expected_ports.items():
        service = services.get(name)
        if service is None:
            continue  # already reported as missing above
        port = find_port(service, target)
        if port is None:
            errors.append(f"{name}: no published port maps to container port {target}")
            continue
        published = port.get("published")
        if expected_published is not None and str(published) != expected_published:
            errors.append(f"{name}: expected published port {expected_published} -> {target}, got {published!r}")
        elif published in (None, ""):
            errors.append(f"{name}: container port {target} has no published host port")

    for name in sorted(healthcheck_required):
        service = services.get(name)
        if service is None:
            continue
        healthcheck = service.get("healthcheck")
        if not healthcheck or not healthcheck.get("test"):
            errors.append(f"{name}: no healthcheck defined")

    worker = services.get("worker")
    if worker is not None:
        depends_on = worker.get("depends_on", {})
        for dep_name, expected_condition in WORKER_DEPENDS_ON.items():
            dep = depends_on.get(dep_name)
            if dep is None:
                errors.append(f"worker: missing depends_on entry for {dep_name}")
                continue
            condition = dep.get("condition") if isinstance(dep, dict) else None
            if condition != expected_condition:
                errors.append(f"worker: depends_on[{dep_name}] expected condition {expected_condition!r}, got {condition!r}")
        unexpected_deps = set(depends_on) - set(WORKER_DEPENDS_ON)
        if unexpected_deps:
            errors.append(f"worker: unexpected depends_on entries: {sorted(unexpected_deps)}")

    if observability:
        for name in OTLP_SERVICES:
            service = services.get(name)
            if service is None:
                continue
            actual = environment_of(service).get(OTLP_VARIABLE, "")
            if actual != OTLP_ENDPOINT:
                errors.append(f"{name}: expected {OTLP_VARIABLE}={OTLP_ENDPOINT}, got {actual!r}")

    return errors


def main(argv: list[str]) -> int:
    unknown = [arg for arg in argv if arg != "--observability"]
    if unknown:
        print(f"compose_check: unknown arguments {unknown}; the only flag is --observability", file=sys.stderr)
        return 2
    observability = "--observability" in argv

    try:
        config = json.loads(sys.stdin.read())
    except json.JSONDecodeError as ex:
        print(f"compose_check: stdin was not valid JSON (expected `<compose> config --format json` output): {ex}", file=sys.stderr)
        return 2

    errors = check(config, observability)
    if errors:
        print("compose_check: FAILED", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1

    scope = "base + observability" if observability else "base"
    print(f"compose_check: OK ({scope}) — services, published ports, healthchecks and worker depends_on match CT-005")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
```

- [ ] **Step 4: Run the tests and the real file**

Run: `python3 -m unittest discover -s tools/tests -v`
Expected: 11 tests, OK.

Run: `make compose-check`
Expected: `compose_check: OK (base) — …`

- [ ] **Step 5: Add the unittest step to CI**

In `.github/workflows/ci.yml`, contracts job, directly after `- run: python3 tools/docs_check.py`:

```yaml
      - name: tooling unit tests (compose_check, observability assets, image pins)
        run: python3 -m unittest discover -s tools/tests -v
```

- [ ] **Step 6: Commit**

```bash
git add tools/compose_check.py tools/tests/test_compose_check.py .github/workflows/ci.yml
git commit -m "feat(tools): unit-tested compose_check with an --observability mode (CT-005)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Observability stack — images, compose file, Makefile, CI

**Files:**
- Create: `deploy/observability/prometheus/Dockerfile`, `deploy/observability/prometheus/prometheus.yml`
- Create: `deploy/observability/grafana/Dockerfile`, `deploy/observability/grafana/provisioning/datasources/prometheus.yml`, `deploy/observability/grafana/provisioning/dashboards/crm.yml`, `deploy/observability/grafana/dashboards/crm-overview.json`
- Create: `deploy/docker-compose.observability.yml`
- Create: `tools/tests/test_observability_assets.py`
- Modify: `deploy/.env.example`, `Makefile`, `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: `compose_check.py --observability` expectations from Task 2 (service names, container ports, healthchecks, OTLP endpoint value).
- Produces: `make up-observability`, `make tunnel-observability`; host ports Prometheus 9090, Grafana 3001, Jaeger 16686; dashboard uid `crm-overview`; datasource uid `prometheus`.

Metric names the dashboard queries. The first is exported today by the ASP.NET Core meter; the `crm_*` ones are defined in `docs/superpowers/specs/2026-09-15-crm-slice1-design.md` §observability and are emitted by later plans (panels show "No data" until then, as spec §8.2 says): `http_server_request_duration_seconds`, `crm_ingest_events_total`, `crm_ingest_rejected_total`, `crm_consumer_lag`, `crm_dlq_total`, `crm_segment_run_seconds`, `crm_realtime_eval_seconds`.

- [ ] **Step 1: Write the failing asset tests**

`tools/tests/test_observability_assets.py`:

```python
"""Invariants of the baked observability assets. Run: python3 -m unittest discover -s tools/tests -v"""
from __future__ import annotations

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OBSERVABILITY = ROOT / "deploy" / "observability"
DASHBOARD = OBSERVABILITY / "grafana" / "dashboards" / "crm-overview.json"

REQUIRED_METRICS = [
    "http_server_request_duration_seconds",
    "crm_ingest_events_total",
    "crm_ingest_rejected_total",
    "crm_consumer_lag",
    "crm_dlq_total",
    "crm_segment_run_seconds",
    "crm_realtime_eval_seconds",
]


class DashboardTests(unittest.TestCase):
    def setUp(self):
        self.dashboard = json.loads(DASHBOARD.read_text(encoding="utf-8"))

    def test_identity(self):
        self.assertEqual(self.dashboard["uid"], "crm-overview")
        self.assertEqual(self.dashboard["title"], "CRM overview")

    def test_every_panel_uses_the_provisioned_datasource(self):
        for panel in self.dashboard["panels"]:
            self.assertEqual(panel["datasource"], {"type": "prometheus", "uid": "prometheus"}, panel["title"])
            for target in panel["targets"]:
                self.assertEqual(target["datasource"], {"type": "prometheus", "uid": "prometheus"}, panel["title"])

    def test_panel_ids_are_unique(self):
        ids = [panel["id"] for panel in self.dashboard["panels"]]
        self.assertEqual(len(ids), len(set(ids)))

    def test_every_required_metric_is_queried(self):
        expressions = " ".join(t["expr"] for p in self.dashboard["panels"] for t in p["targets"])
        for metric in REQUIRED_METRICS:
            self.assertIn(metric, expressions)


class ProvisioningTests(unittest.TestCase):
    def test_datasource_points_at_the_prometheus_service_with_the_uid_the_dashboard_uses(self):
        text = (OBSERVABILITY / "grafana" / "provisioning" / "datasources" / "prometheus.yml").read_text(encoding="utf-8")
        self.assertIn("uid: prometheus", text)
        self.assertIn("url: http://prometheus:9090", text)

    def test_dashboard_provider_reads_the_directory_the_image_copies_into(self):
        provider = (OBSERVABILITY / "grafana" / "provisioning" / "dashboards" / "crm.yml").read_text(encoding="utf-8")
        dockerfile = (OBSERVABILITY / "grafana" / "Dockerfile").read_text(encoding="utf-8")
        self.assertIn("path: /etc/grafana/dashboards", provider)
        self.assertIn("COPY dashboards /etc/grafana/dashboards", dockerfile)

    def test_prometheus_scrapes_api_and_worker(self):
        text = (OBSERVABILITY / "prometheus" / "prometheus.yml").read_text(encoding="utf-8")
        self.assertIn("api:8080", text)
        self.assertIn("worker:8081", text)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run to verify failure**

Run: `python3 -m unittest discover -s tools/tests -v`
Expected: `test_observability_assets` errors with `FileNotFoundError`; `test_compose_check` still passes.

- [ ] **Step 3: Prometheus image**

`deploy/observability/prometheus/prometheus.yml`:

```yaml
# Baked into the image (deploy/observability/prometheus/Dockerfile): the engine may be remote, so
# nothing under deploy/ can be bind-mounted. Rebuild with `make up-observability` after editing.
global:
  scrape_interval: 5s
  evaluation_interval: 15s

scrape_configs:
  - job_name: crm-api
    metrics_path: /metrics
    static_configs:
      - targets: ["api:8080"]
  - job_name: crm-worker
    metrics_path: /metrics
    static_configs:
      - targets: ["worker:8081"]
```

`deploy/observability/prometheus/Dockerfile`:

```dockerfile
FROM prom/prometheus:v3.5.0
COPY prometheus.yml /etc/prometheus/prometheus.yml
```

- [ ] **Step 4: Grafana image**

`deploy/observability/grafana/Dockerfile`:

```dockerfile
FROM grafana/grafana:12.1.1
COPY provisioning /etc/grafana/provisioning
COPY dashboards /etc/grafana/dashboards
```

`deploy/observability/grafana/provisioning/datasources/prometheus.yml`:

```yaml
apiVersion: 1
datasources:
  - name: Prometheus
    uid: prometheus
    type: prometheus
    access: proxy
    url: http://prometheus:9090
    isDefault: true
    editable: false
```

`deploy/observability/grafana/provisioning/dashboards/crm.yml`:

```yaml
apiVersion: 1
providers:
  - name: crm
    folder: CRM
    type: file
    disableDeletion: true
    allowUiUpdates: false
    options:
      path: /etc/grafana/dashboards
```

`deploy/observability/grafana/dashboards/crm-overview.json`:

```json
{
  "uid": "crm-overview",
  "title": "CRM overview",
  "tags": ["crm"],
  "timezone": "browser",
  "schemaVersion": 41,
  "version": 1,
  "refresh": "10s",
  "time": { "from": "now-15m", "to": "now" },
  "panels": [
    {
      "id": 1,
      "type": "stat",
      "title": "Scrape targets up",
      "gridPos": { "h": 4, "w": 24, "x": 0, "y": 0 },
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "targets": [
        { "refId": "A", "datasource": { "type": "prometheus", "uid": "prometheus" }, "expr": "up{job=~\"crm-.*\"}", "legendFormat": "{{job}}" }
      ]
    },
    {
      "id": 2,
      "type": "timeseries",
      "title": "HTTP request rate (req/s)",
      "gridPos": { "h": 8, "w": 12, "x": 0, "y": 4 },
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "targets": [
        { "refId": "A", "datasource": { "type": "prometheus", "uid": "prometheus" }, "expr": "sum by (job) (rate(http_server_request_duration_seconds_count[1m]))", "legendFormat": "{{job}}" }
      ]
    },
    {
      "id": 3,
      "type": "timeseries",
      "title": "HTTP latency p95 (s)",
      "gridPos": { "h": 8, "w": 12, "x": 12, "y": 4 },
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "targets": [
        { "refId": "A", "datasource": { "type": "prometheus", "uid": "prometheus" }, "expr": "histogram_quantile(0.95, sum by (le, job) (rate(http_server_request_duration_seconds_bucket[5m])))", "legendFormat": "{{job}}" }
      ]
    },
    {
      "id": 4,
      "type": "timeseries",
      "title": "Ingest rate (events/s)",
      "description": "Emitted by the Ingestion plan; empty until then.",
      "gridPos": { "h": 8, "w": 12, "x": 0, "y": 12 },
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "targets": [
        { "refId": "A", "datasource": { "type": "prometheus", "uid": "prometheus" }, "expr": "sum(rate(crm_ingest_events_total[1m]))", "legendFormat": "accepted" },
        { "refId": "B", "datasource": { "type": "prometheus", "uid": "prometheus" }, "expr": "sum(rate(crm_ingest_rejected_total[1m]))", "legendFormat": "rejected" }
      ]
    },
    {
      "id": 5,
      "type": "timeseries",
      "title": "Consumer lag (messages)",
      "description": "Emitted by the Profiles plan; empty until then.",
      "gridPos": { "h": 8, "w": 12, "x": 12, "y": 12 },
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "targets": [
        { "refId": "A", "datasource": { "type": "prometheus", "uid": "prometheus" }, "expr": "sum by (topic) (crm_consumer_lag)", "legendFormat": "{{topic}}" }
      ]
    },
    {
      "id": 6,
      "type": "timeseries",
      "title": "Dead-lettered messages (5 m increase)",
      "description": "Emitted by the Profiles plan; empty until then.",
      "gridPos": { "h": 8, "w": 8, "x": 0, "y": 20 },
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "targets": [
        { "refId": "A", "datasource": { "type": "prometheus", "uid": "prometheus" }, "expr": "sum by (topic) (increase(crm_dlq_total[5m]))", "legendFormat": "{{topic}}" }
      ]
    },
    {
      "id": 7,
      "type": "timeseries",
      "title": "Segment run duration p95 (s)",
      "description": "Emitted by the Segmentation plan; empty until then.",
      "gridPos": { "h": 8, "w": 8, "x": 8, "y": 20 },
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "targets": [
        { "refId": "A", "datasource": { "type": "prometheus", "uid": "prometheus" }, "expr": "histogram_quantile(0.95, sum by (le) (rate(crm_segment_run_seconds_bucket[5m])))", "legendFormat": "p95" }
      ]
    },
    {
      "id": 8,
      "type": "timeseries",
      "title": "Real-time evaluation p95 (s)",
      "description": "Emitted by the Segmentation plan; empty until then.",
      "gridPos": { "h": 8, "w": 8, "x": 16, "y": 20 },
      "datasource": { "type": "prometheus", "uid": "prometheus" },
      "targets": [
        { "refId": "A", "datasource": { "type": "prometheus", "uid": "prometheus" }, "expr": "histogram_quantile(0.95, sum by (le) (rate(crm_realtime_eval_seconds_bucket[5m])))", "legendFormat": "p95" }
      ]
    }
  ]
}
```

- [ ] **Step 5: Compose file**

`deploy/docker-compose.observability.yml`:

```yaml
# Opt-in observability stack (spec §8.2). Used together with docker-compose.yml:
#   make up-observability
# Prometheus and Grafana configuration is baked into images under deploy/observability/ —
# no bind mounts, because the engine may be remote (see `make tunnel`).

services:
  api:
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: http://jaeger:4317

  worker:
    environment:
      OTEL_EXPORTER_OTLP_ENDPOINT: http://jaeger:4317

  prometheus:
    profiles: [observability]
    build:
      context: observability/prometheus
    ports:
      - "${PROMETHEUS_HOST_PORT:-9090}:9090"
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://127.0.0.1:9090/-/healthy"]
      interval: 10s
      timeout: 5s
      retries: 12

  grafana:
    profiles: [observability]
    build:
      context: observability/grafana
    environment:
      # Local development only: anonymous read-only access, so the dashboard opens without a login.
      GF_AUTH_ANONYMOUS_ENABLED: "true"
      GF_AUTH_ANONYMOUS_ORG_ROLE: Viewer
      GF_SECURITY_ADMIN_PASSWORD: ${GRAFANA_ADMIN_PASSWORD:-admin}
      GF_DASHBOARDS_DEFAULT_HOME_DASHBOARD_PATH: /etc/grafana/dashboards/crm-overview.json
      GF_ANALYTICS_REPORTING_ENABLED: "false"
      GF_ANALYTICS_CHECK_FOR_UPDATES: "false"
    ports:
      # Web owns host port 3000.
      - "${GRAFANA_HOST_PORT:-3001}:3000"
    depends_on:
      prometheus:
        condition: service_healthy
    healthcheck:
      test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:3000/api/health >/dev/null"]
      interval: 10s
      timeout: 5s
      retries: 12
      start_period: 10s

  jaeger:
    profiles: [observability]
    # Jaeger v2 (v1 all-in-one is end-of-life). OTLP gRPC on 4317 inside the compose network, UI on 16686.
    # The image ships no shell, so there is no healthcheck.
    image: jaegertracing/jaeger:2.10.0
    ports:
      - "${JAEGER_HOST_PORT:-16686}:16686"
```

Verify the three base image tags exist before going further (read-only registry queries; no engine needed):

```bash
for ref in prom/prometheus:v3.5.0 grafana/grafana:12.1.1 jaegertracing/jaeger:2.10.0; do
  docker manifest inspect "docker.io/$ref" >/dev/null 2>&1 && echo "ok   $ref" || echo "MISSING $ref"
done
```

Expected: three `ok` lines. If a tag is `MISSING` (or `docker manifest` is unavailable), query `https://hub.docker.com/v2/repositories/<repo>/tags?page_size=50&name=<major>.` with `curl`, pick the highest patch of the same major.minor line, use it in the file, and name the substitution in your report.

- [ ] **Step 6: `.env.example`**

Append to `deploy/.env.example`:

```bash
# Observability profile (`make up-observability`). Host-side ports; Grafana avoids 3000 (web).
PROMETHEUS_HOST_PORT=9090
GRAFANA_HOST_PORT=3001
JAEGER_HOST_PORT=16686
# Grafana admin password (anonymous Viewer access is enabled for local development).
GRAFANA_ADMIN_PASSWORD=admin
```

- [ ] **Step 7: Makefile**

After the `COMPOSE :=` line add:

```make
# Base + observability files with the profile switched on. `down` and `ps` always use this form so
# observability containers are never orphaned; `up` stays base-only and `up-observability` opts in.
COMPOSE_OBS  := $(COMPOSE) -f deploy/docker-compose.observability.yml --profile observability
```

After `REDIS_HOST_PORT  ?= 6380` add:

```make
PROMETHEUS_HOST_PORT ?= 9090
GRAFANA_HOST_PORT    ?= 3001
JAEGER_HOST_PORT     ?= 16686
```

After the `TUNNEL_APP_PORTS :=` line add:

```make
TUNNEL_OBS_PORTS := -L 9090:localhost:$(PROMETHEUS_HOST_PORT) -L 3001:localhost:$(GRAFANA_HOST_PORT) -L 16686:localhost:$(JAEGER_HOST_PORT)
```

Add `up-observability tunnel-observability` to `.PHONY`.

After the `tunnel-apps` target add:

```make
tunnel-observability: ## like tunnel-apps, also forwards Prometheus 9090, Grafana 3001, Jaeger 16686
	@$(call open_tunnel,$(TUNNEL_PORTS) $(TUNNEL_APP_PORTS) $(TUNNEL_OBS_PORTS),9090,localhost:1433->$(MSSQL_HOST_PORT) 6380 19092 8080 8081 3000 9090 3001 16686)
```

After the `up` target add:

```make
up-observability: env ## start the stack plus Prometheus, Grafana and Jaeger (api/worker export OTLP to Jaeger)
	$(COMPOSE_OBS) up -d --build --wait
	@echo ""
	@echo "Prometheus  http://localhost:9090   (targets: api:8080/metrics, worker:8081/metrics)"
	@echo "Grafana     http://localhost:3001   (dashboard: CRM overview, anonymous Viewer)"
	@echo "Jaeger      http://localhost:16686  (services Crm.Api, Crm.Worker)"
```

Replace the `down`, `ps` and `logs` targets with:

```make
down: ## stop the stack (observability included) and remove volumes (KEEP=1 keeps data)
ifeq ($(KEEP),1)
	$(COMPOSE_OBS) down
else
	$(COMPOSE_OBS) down -v
endif

ps: ## show service status (observability included)
	$(COMPOSE_OBS) ps

# `make logs` follows the two .NET hosts; `make logs SERVICES="kafka redis"` or SERVICES= (all) to widen.
SERVICES ?= api worker

logs: ## follow logs of api and worker (SERVICES="..." to choose others)
	$(COMPOSE_OBS) logs -f --tail=100 $(SERVICES)
```

Replace the `compose-check` target with:

```make
compose-check: ## validate compose services, ports and healthchecks, base and observability (CT-005)
	$(COMPOSE) config --format json | python3 tools/compose_check.py
	$(COMPOSE_OBS) config --format json | python3 tools/compose_check.py --observability
```

- [ ] **Step 8: CI**

In `.github/workflows/ci.yml` replace the `validate compose files` step body with:

```yaml
      - name: validate compose files
        run: |
          cp deploy/.env.example deploy/.env
          base="docker compose --project-directory deploy -f deploy/docker-compose.yml"
          obs="$base -f deploy/docker-compose.observability.yml --profile observability"
          $base config -q
          $base config --format json | python3 tools/compose_check.py
          $obs config -q
          $obs config --format json | python3 tools/compose_check.py --observability
```

- [ ] **Step 9: Verify**

Run: `python3 -m unittest discover -s tools/tests -v` → all pass (18 tests).
Run: `make compose-check` → two `OK` lines, `(base)` then `(base + observability)`.
Run: `make help` → lists `up-observability` and `tunnel-observability`.
Run: `python3 -c "import json;json.load(open('deploy/observability/grafana/dashboards/crm-overview.json'))"` → no output.

Do **not** start the stack (ruling C6); the controller does the live check in Task 8.

- [ ] **Step 10: Commit**

```bash
git add deploy/observability deploy/docker-compose.observability.yml deploy/.env.example Makefile .github/workflows/ci.yml tools/tests/test_observability_assets.py
git commit -m "feat(deploy): opt-in observability profile with Prometheus, Grafana dashboard and Jaeger

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Carried minors — image pins, test ids, data dictionary order

**Files:**
- Modify: `tests/Crm.IntegrationTests/Fixtures/CrmStackFixture.cs:29-31`
- Create: `tools/tests/test_image_pins.py`
- Modify: `tests/Crm.Kernel.UnitTests/RedisKeysTests.cs`, `tests/Crm.Contracts.UnitTests/ContractSnapshotTests.cs` (method renames only)
- Modify: `docs/data-dictionary/redis-and-kafka.md:35`, `docs/test/unit.md` (UT-057 location line)

**Interfaces:** none produced for other tasks.

- [ ] **Step 1: Write the failing image-pin test**

`tools/tests/test_image_pins.py`:

```python
"""Testcontainers image literals must be images the compose stack also runs (ruling C8)."""
from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tests" / "Crm.IntegrationTests" / "Fixtures" / "CrmStackFixture.cs"
COMPOSE = ROOT / "deploy" / "docker-compose.yml"


class ImagePinTests(unittest.TestCase):
    def test_kafka_and_redis_builders_use_the_compose_images(self):
        fixture = FIXTURE.read_text(encoding="utf-8")
        compose = COMPOSE.read_text(encoding="utf-8")
        literals = re.findall(r'new (?:Kafka|Redis)Builder\("([^"]+)"\)', fixture)
        self.assertEqual(len(literals), 2, f"expected one Kafka and one Redis builder literal, found {literals}")
        for image in literals:
            self.assertIn(f"image: {image}", compose, f"{image} is not an image of deploy/docker-compose.yml")


if __name__ == "__main__":
    unittest.main()
```

Run: `python3 -m unittest discover -s tools/tests -v`
Expected: FAIL — `confluentinc/cp-kafka:7.5.12 is not an image of deploy/docker-compose.yml`.

- [ ] **Step 2: Align the fixture**

In `CrmStackFixture.cs` replace the two builder lines with:

```csharp
    // Same images as deploy/docker-compose.yml; tools/tests/test_image_pins.py fails when they drift.
    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:3.9.1").Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();
```

Testcontainers.Kafka 4.15 selects its Apache (KRaft) start-up configuration from the image name; no other change is needed.

Run: `python3 -m unittest discover -s tools/tests -v` → PASS.

- [ ] **Step 3: Run the integration suite on the new images**

Run (Global Constraints environment prefix): `dotnet test Crm.sln --filter "Category=Integration"`
Expected: 24 passed, 0 skipped (23 + the Task 1 test).

If Kafka does not become ready with the Apache image: capture the container log (`docker logs` of the `apache/kafka` container via the tunnel socket is read-only and allowed), make one diagnosis pass, and if it is not a one-line fix report `BLOCKED` with the log — do not silently revert to the Confluent image.

- [ ] **Step 4: Carry case ids in test names**

`tests/Crm.Kernel.UnitTests/RedisKeysTests.cs` — rename (bodies unchanged):
- `Api_key_cache_and_channel_are_the_only_unprefixed_keys` → `UT057_api_key_cache_and_channel_are_the_only_unprefixed_keys`
- `Every_tenant_scoped_builder_starts_with_tenant_prefix` → `UT057_every_tenant_scoped_builder_starts_with_the_tenant_prefix`

`tests/Crm.Contracts.UnitTests/ContractSnapshotTests.cs` — rename (bodies unchanged):
- `Serialized_message_matches_committed_snapshot` → `CT004_serialized_message_matches_committed_snapshot`
- `Snapshot_round_trips_through_deserialization` → `CT004_snapshot_round_trips_through_deserialization`
- `Topic_and_header_constants_match_the_data_dictionary` → `CT004_topic_and_header_constants_match_the_data_dictionary`

In `docs/test/unit.md`, UT-057's last bullet names a file that does not exist (`RedisKeyBuilderTests.cs`). Change that bullet to:

```markdown
- Implemented: tests/Crm.Kernel.UnitTests/RedisKeysTests.cs
```

Run: `dotnet test Crm.sln --filter "Category!=Integration"` → all green, same counts as before (Contracts 11, Kernel 19).

- [ ] **Step 5: Data dictionary field order**

`ProfileUpdated` in `src/Shared/Crm.Contracts/Messages.cs` is `(TenantId, ProfileId, ChangedKeys, Profile, OccurredAt)`; the snapshot serializes in that order. In `docs/data-dictionary/redis-and-kafka.md` replace the `ProfileUpdated` row with:

```markdown
| `ProfileUpdated` | tenantId, profileId, changedKeys (string[]), profile: { externalId?, email?, phone?, attributes }, occurredAt |
```

Run: `make docs-check` → `0 errors`.

- [ ] **Step 6: Commit**

```bash
git add tests/Crm.IntegrationTests/Fixtures/CrmStackFixture.cs tools/tests/test_image_pins.py tests/Crm.Kernel.UnitTests/RedisKeysTests.cs tests/Crm.Contracts.UnitTests/ContractSnapshotTests.cs docs/data-dictionary/redis-and-kafka.md docs/test/unit.md
git commit -m "test: run integration tests on the compose images, carry case ids in test names, fix ProfileUpdated field order in the data dictionary

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: Web — `Input` and `Card` primitives, node tsconfig typecheck

Spec §7.3 promises shadcn/ui `button, input, card`; only `button` exists. The shadcn CLI is interactive, so the components are hand-written in the same style as `src/components/ui/button.tsx` (function components, `cn`, zinc palette).

All paths below are relative to `src/Hosts/Crm.Web/`.

**Files:**
- Create: `src/components/ui/input.tsx`, `src/components/ui/input.test.tsx`, `src/components/ui/card.tsx`, `src/components/ui/card.test.tsx`, `src/routes/login.test.tsx`
- Modify: `src/routes/login.tsx`, `src/routes/home.tsx`, `package.json` (`typecheck`, `build` scripts)

**Interfaces:**
- Produces: `Input(props: React.ComponentProps<'input'>)`; `Card`, `CardHeader`, `CardContent` (`React.ComponentProps<'div'>`), `CardTitle(props: React.ComponentProps<'h2'> & { as?: 'h1' | 'h2' | 'h3' })`.
- Must keep: e2e `e2e/smoke.spec.ts` finds a heading matching `/Platform status: Healthy/`, test ids `check-<name>` with text `<name>: Healthy`, and a disabled button matching `/Sign in/` on `/login`.

- [ ] **Step 1: Failing tests**

`src/components/ui/input.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { Input } from './input';

describe('Input', () => {
  it('is reachable by its label and keeps caller classes', () => {
    render(
      <>
        <label htmlFor="email">Email</label>
        <Input id="email" type="email" className="w-40" />
      </>,
    );
    const input = screen.getByLabelText('Email') as HTMLInputElement;
    expect(input.type).toBe('email');
    expect(input.className).toContain('w-40');
    expect(input.className).toContain('rounded-md');
  });

  it('defaults to a text input and forwards disabled', () => {
    render(<Input aria-label="Name" disabled />);
    const input = screen.getByLabelText('Name') as HTMLInputElement;
    expect(input.type).toBe('text');
    expect(input.disabled).toBe(true);
  });
});
```

`src/components/ui/card.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { Card, CardContent, CardHeader, CardTitle } from './card';

describe('Card', () => {
  it('renders the title as a level-2 heading by default and shows its content', () => {
    render(
      <Card>
        <CardHeader>
          <CardTitle>Platform</CardTitle>
        </CardHeader>
        <CardContent>body text</CardContent>
      </Card>,
    );
    expect(screen.getByRole('heading', { level: 2, name: 'Platform' })).toBeTruthy();
    expect(screen.getByText('body text')).toBeTruthy();
  });

  it('lets a page promote the title to its h1', () => {
    render(<CardTitle as="h1">Sign in</CardTitle>);
    expect(screen.getByRole('heading', { level: 1, name: 'Sign in' })).toBeTruthy();
  });
});
```

`src/routes/login.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { LoginPage } from './login';

describe('LoginPage', () => {
  it('labels both fields and keeps submit disabled until the Web plan wires auth', () => {
    render(<LoginPage />);
    expect((screen.getByLabelText('Email') as HTMLInputElement).type).toBe('email');
    expect((screen.getByLabelText('Password') as HTMLInputElement).type).toBe('password');
    expect((screen.getByRole('button', { name: /Sign in/ }) as HTMLButtonElement).disabled).toBe(true);
  });
});
```

Run: `npm test -- --run`
Expected: FAIL — cannot resolve `./input`, `./card`.

- [ ] **Step 2: Components**

`src/components/ui/input.tsx`:

```tsx
import * as React from 'react';
import { cn } from '@/lib/utils';

export type InputProps = React.ComponentProps<'input'>;

export function Input({ className, type = 'text', ...props }: InputProps) {
  return (
    <input
      type={type}
      className={cn(
        'flex h-9 w-full rounded-md border border-zinc-300 bg-transparent px-3 py-1 text-sm shadow-sm transition-colors placeholder:text-zinc-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-zinc-400 disabled:cursor-not-allowed disabled:opacity-50 dark:border-zinc-700',
        className,
      )}
      {...props}
    />
  );
}
```

`src/components/ui/card.tsx`:

```tsx
import * as React from 'react';
import { cn } from '@/lib/utils';

export function Card({ className, ...props }: React.ComponentProps<'div'>) {
  return (
    <div
      className={cn('rounded-lg border border-zinc-200 bg-white shadow-sm dark:border-zinc-800 dark:bg-zinc-900', className)}
      {...props}
    />
  );
}

export function CardHeader({ className, ...props }: React.ComponentProps<'div'>) {
  return <div className={cn('flex flex-col gap-1.5 p-6 pb-3', className)} {...props} />;
}

export type CardTitleProps = React.ComponentProps<'h2'> & { as?: 'h1' | 'h2' | 'h3' };

export function CardTitle({ as: Heading = 'h2', className, ...props }: CardTitleProps) {
  return <Heading className={cn('text-xl font-semibold leading-none tracking-tight', className)} {...props} />;
}

export function CardContent({ className, ...props }: React.ComponentProps<'div'>) {
  return <div className={cn('p-6 pt-3', className)} {...props} />;
}
```

- [ ] **Step 3: Use them in the routes**

`src/routes/login.tsx` (whole file):

```tsx
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

export function LoginPage() {
  return (
    <Card className="max-w-sm">
      <CardHeader>
        <CardTitle as="h1">Sign in</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="space-y-4" onSubmit={(e) => e.preventDefault()}>
          <div className="space-y-1 text-sm">
            <label htmlFor="email">Email</label>
            <Input id="email" name="email" type="email" autoComplete="username" />
          </div>
          <div className="space-y-1 text-sm">
            <label htmlFor="password">Password</label>
            <Input id="password" name="password" type="password" autoComplete="current-password" />
          </div>
          <Button type="submit" disabled>
            Sign in (wired by the Web plan)
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}
```

`src/routes/home.tsx` — replace only the final `return (...)` block:

```tsx
  return (
    <Card className="max-w-md">
      <CardHeader>
        <CardTitle as="h1">Platform status: {data.status}</CardTitle>
      </CardHeader>
      <CardContent>
        <ul className="space-y-1 text-sm">
          {Object.entries(data.checks).map(([name, status]) => (
            <li key={name} data-testid={`check-${name}`}>
              {name}: {status}
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  );
```

and add the import `import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';` below the existing imports.

- [ ] **Step 4: Typecheck both tsconfigs**

In `package.json` change two scripts:

```json
    "build": "npm run typecheck && vite build",
    "typecheck": "tsc -p tsconfig.app.json --noEmit && tsc -p tsconfig.node.json --noEmit",
```

(`tsconfig.node.json` covers `vite.config.ts` and `playwright.config.ts`, which nothing type-checked before.)

- [ ] **Step 5: Verify**

Run: `npm test -- --run` → 7 tests pass (2 button, 2 input, 2 card, 1 login).
Run: `npm run typecheck` → no output, exit 0.
Run: `npm run lint` → clean. `npx prettier --check src` → clean (run `npx prettier --write` on files you created if not).
Run: `npm run build` → succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Hosts/Crm.Web/src src/Hosts/Crm.Web/package.json
git commit -m "feat(web): Input and Card primitives on the login and status pages; typecheck the node tsconfig

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: Dependabot

**Files:**
- Create: `.github/dependabot.yml`

- [ ] **Step 1: Write the configuration**

```yaml
# Dependency updates (foundation spec §13: "package versions drift across projects").
# Weekly, grouped so one ecosystem produces a handful of pull requests rather than dozens.
version: 2
updates:
  - package-ecosystem: nuget
    directory: /
    schedule: { interval: weekly, day: monday }
    open-pull-requests-limit: 5
    groups:
      opentelemetry:
        patterns: ["OpenTelemetry*"]
      microsoft:
        patterns: ["Microsoft.*", "System.*"]
      test-tooling:
        patterns: ["xunit*", "FluentAssertions", "Testcontainers*", "NetArchTest*", "coverlet*"]

  - package-ecosystem: dotnet-sdk
    directory: /
    schedule: { interval: weekly, day: monday }

  - package-ecosystem: npm
    directory: /src/Hosts/Crm.Web
    schedule: { interval: weekly, day: monday }
    open-pull-requests-limit: 5
    groups:
      tanstack:
        patterns: ["@tanstack/*"]
      react:
        patterns: ["react", "react-dom", "@types/react", "@types/react-dom"]
      build-and-test:
        patterns: ["vite", "vitest", "@vitejs/*", "typescript", "typescript-eslint", "eslint*", "@eslint/*", "prettier", "jsdom", "@testing-library/*", "@playwright/test", "tailwindcss", "@tailwindcss/*"]

  - package-ecosystem: docker
    directories:
      - /src/Hosts/Crm.Api
      - /src/Hosts/Crm.Worker
      - /src/Hosts/Crm.Web
      - /deploy/observability/prometheus
      - /deploy/observability/grafana
    schedule: { interval: weekly, day: monday }

  - package-ecosystem: docker-compose
    directory: /deploy
    schedule: { interval: weekly, day: monday }

  - package-ecosystem: github-actions
    directory: /
    schedule: { interval: weekly, day: monday }
```

- [ ] **Step 2: Validate**

Group patterns must not overlap (Dependabot assigns a dependency to the first matching group in declaration order, so an overlap silently misfiles it): `Microsoft.NET.Test.Sdk` belongs to `microsoft` and is deliberately absent from `test-tooling`.

Run: `python3 - <<'EOF'
import re, pathlib
text = pathlib.Path(".github/dependabot.yml").read_text()
ecosystems = re.findall(r"package-ecosystem: (\S+)", text)
assert ecosystems == ["nuget", "dotnet-sdk", "npm", "docker", "docker-compose", "github-actions"], ecosystems
for d in re.findall(r"^\s+- (/\S+)$", text, flags=re.M) + re.findall(r"directory: (/\S+)", text):
    assert pathlib.Path("." + d).is_dir(), d
print("dependabot.yml: ecosystems and directories ok")
EOF`
Expected: `dependabot.yml: ecosystems and directories ok`. (Requires Task 3's `deploy/observability/*` directories.)

- [ ] **Step 3: Commit**

```bash
git add .github/dependabot.yml
git commit -m "chore(deps): weekly grouped Dependabot updates for NuGet, the SDK, npm, images and actions

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: LT-005 cold-start script

**Files:**
- Create: `tests/Crm.LoadTests/coldstart.sh` (executable)
- Modify: `docs/test/e2e-load.md` (LT-005 block)

**Interfaces:**
- Produces: `tests/Crm.LoadTests/coldstart.sh [MAKE_VAR=value ...]` — runs `make up`, `make seed`, the quickstart probe; prints a Markdown table row on the last line of stdout; exit 0 within budgets, 1 over budget or probe failure, 2 when the stack is already running. Task 8 (controller) runs it; Task 9 pastes its row into the README.

- [ ] **Step 1: Write the script**

```bash
#!/usr/bin/env bash
# LT-005 — cold start and quickstart (docs/test/e2e-load.md).
#
#   tests/Crm.LoadTests/coldstart.sh                          # local engine
#   tests/Crm.LoadTests/coldstart.sh MSSQL_HOST_PORT=11433    # extra args are passed to make
#
# Foundation scope (plan C, ruling C3): budgets are `make up` <= 180 s and `make seed` <= 120 s, and the
# quickstart probe is "log in as the seeded Admin and list API keys -> 200". The Ingestion plan changes
# the probe to POST /v1/track -> 202; the Profiles plan adds the 5k-profile / 100k-event seed volume.
#
# Preconditions: no container of the compose project is running (`make down` first), and with a remote
# engine the app tunnel is already up (`make tunnel-apps`), because seeding and the probe use localhost.
set -euo pipefail

cd "$(dirname "$0")/../.."

BASE_URL="${CRM_BASE_URL:-http://localhost:8080}"
MACHINE="${LT005_MACHINE:-$(uname -sm)}"
UP_BUDGET=180
SEED_BUDGET=120

if [ -n "$(docker ps -q --filter label=com.docker.compose.project=crm)" ]; then
  echo "LT-005 measures a cold start, but containers of compose project 'crm' are running. Run 'make down' first." >&2
  exit 2
fi

now() { date +%s; }

started=$(now)
make up "$@"
up_seconds=$(( $(now) - started ))

started=$(now)
make seed "$@"
seed_seconds=$(( $(now) - started ))

token=$(curl -fsS -X POST "$BASE_URL/v1/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@demo.local","password":"Demo-Passw0rd!"}' \
  | python3 -c 'import json, sys; print(json.load(sys.stdin)["accessToken"])')
probe_status=$(curl -s -o /dev/null -w '%{http_code}' "$BASE_URL/v1/api-keys" -H "Authorization: Bearer $token")

verdict=pass
[ "$up_seconds" -le "$UP_BUDGET" ] || verdict=fail
[ "$seed_seconds" -le "$SEED_BUDGET" ] || verdict=fail
[ "$probe_status" = "200" ] || verdict=fail

echo ""
echo "| Case | make up | budget | make seed | budget | probe | verdict | date | machine |"
echo "|---|---|---|---|---|---|---|---|---|"
echo "| LT-005 (foundation scope) | ${up_seconds} s | ${UP_BUDGET} s | ${seed_seconds} s | ${SEED_BUDGET} s | ${probe_status} | ${verdict} | $(date +%Y-%m-%d) | ${MACHINE} |"

[ "$verdict" = pass ]
```

- [ ] **Step 2: Static checks (the script is not executed by a subagent — ruling C6)**

Run: `chmod +x tests/Crm.LoadTests/coldstart.sh && bash -n tests/Crm.LoadTests/coldstart.sh && echo syntax-ok`
Expected: `syntax-ok`.
Run: `command -v shellcheck >/dev/null && shellcheck tests/Crm.LoadTests/coldstart.sh || echo "shellcheck not installed — skipped"`
Expected: no findings, or the skip message.
Run: `git ls-files --stage tests/Crm.LoadTests/coldstart.sh` after `git add` → mode `100755`.

- [ ] **Step 3: Amend LT-005 in `docs/test/e2e-load.md`**

Replace the `- Planned: documented manual step …` bullet of the LT-005 block with:

```markdown
- Implemented (foundation scope): `tests/Crm.LoadTests/coldstart.sh` measures `make up` (≤ 3 min) and `make seed` (≤ 2 min) and runs the quickstart probe; results table in the root README. Until `/v1/track` exists the probe is "log in as the seeded Admin, `GET /v1/api-keys` → 200"; the Ingestion plan switches it to the README curl returning 202, and the Profiles plan adds the 5k-profile / 100k-event seed volume. The case counts as passing only once all three hold.
```

Run: `make docs-check` → `0 errors`.

- [ ] **Step 4: Commit**

```bash
git add tests/Crm.LoadTests/coldstart.sh docs/test/e2e-load.md
git commit -m "test(load): LT-005 cold-start script at foundation scope

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Live verification and LT-005 walkthrough (controller-run, no subagent)

Ruling C6: this task restarts the live stack and the tunnel, so the controller executes it inline. Nothing is committed here; the outputs feed Task 9.

- [ ] **Step 1: Observability stack, live**

```bash
make tunnel-observability
make up-observability MSSQL_HOST_PORT=11433 MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest
curl -s http://localhost:8080/health/ready >/dev/null   # one traced, measured request
```

If the engine reports a host port already in use for 9090 / 3001 / 16686, rerun with e.g. `PROMETHEUS_HOST_PORT=19090` on **both** make commands (the tunnel maps local 9090 to it).

Checks (all must hold):

```bash
curl -s 'http://localhost:9090/api/v1/targets?state=active' | python3 -c 'import json,sys; t=json.load(sys.stdin)["data"]["activeTargets"]; print(sorted((x["labels"]["job"], x["health"]) for x in t))'
# expected: [('crm-api', 'up'), ('crm-worker', 'up')]
curl -s 'http://localhost:9090/api/v1/query?query=http_server_request_duration_seconds_count' | python3 -c 'import json,sys; print(len(json.load(sys.stdin)["data"]["result"]) > 0)'
# expected: True
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:3001/api/dashboards/uid/crm-overview
# expected: 200 (anonymous Viewer)
curl -s http://localhost:16686/api/services | python3 -c 'import json,sys; print(sorted(json.load(sys.stdin)["data"]))'
# expected: contains 'Crm.Api' (and 'Crm.Worker' once it has emitted a span)
```

Open `http://localhost:3001` once and confirm the two HTTP panels draw lines and the five `crm_*` panels say "No data".

- [ ] **Step 2: LT-005 from a fresh clone**

```bash
make down MSSQL_HOST_PORT=11433                 # cold: project 'crm' fully stopped, volumes removed
make tunnel-apps                                # app ports for seed + probe
git clone --quiet "$(pwd)" "$SCRATCH/lt005" && cd "$SCRATCH/lt005"
LT005_MACHINE="home-lab: Rocky 10.2, 4 vCPU, 15 GB, Podman 5.8 (remote over ssh); images cached" \
  tests/Crm.LoadTests/coldstart.sh MSSQL_HOST_PORT=11433
```

Record the full last table row. Exit code 0 is the plan's exit gate (spec §11). Then `make down MSSQL_HOST_PORT=11433` inside the clone, delete the clone, and bring the working checkout's stack back with `make up MSSQL_HOST_PORT=11433 MSSQL_IMAGE=mcr.microsoft.com/mssql/server:2022-latest` + `make seed`.

- [ ] **Step 3: Ledger**

Write the observed check outputs, any port substitution, and the LT-005 row into the SDD ledger so Task 9's implementer receives them verbatim.

---

### Task 9: Final documentation — README, dev-setup, spec, CT-005

**Files:**
- Modify: `README.md`, `docs/dev-setup.md`, `docs/superpowers/specs/2026-09-16-foundation-design.md`, `docs/test/architecture-contract.md`

**Interfaces:**
- Consumes: the LT-005 table row and any port substitution from Task 8 (given to you by the controller in the task prompt — use the row **verbatim**; do not invent or round numbers).

- [ ] **Step 1: README**

Replace the Status paragraph and table rows up to and including `Implementation: Tenancy` with:

```markdown
Slice 1 (events → profiles → segments) has its foundation complete: solution structure, Kernel, Contracts, the API/Worker/Web hosts, multi-tenant auth, CI, and an opt-in observability stack. Ingestion, Profiles and Segmentation land next, each behind its own plan under `docs/superpowers/plans/`.

| Phase | State |
|---|---|
| Design spec | Approved |
| Documentation (BRD, ADRs, architecture, data dictionary, API reference) | Complete |
| Implementation: solution skeleton, Kernel, Contracts, hosts, web shell, CI | Complete |
| Implementation: Tenancy | Complete |
| Implementation: developer experience (observability profile, Dependabot, cold-start measurement) | Complete |
```

(keep the remaining `Planned` rows unchanged).

Replace everything from `## Quickstart` down to (not including) `## Roadmap` with:

````markdown
## Quickstart

Three commands from a fresh clone:

```bash
make up          # infrastructure + api + worker + web, builds images, waits until healthy
make seed        # demo tenant, Admin + Viewer users, an API key written to .env.local
curl -s -X POST http://localhost:8080/v1/auth/login -H "Content-Type: application/json" \
  -d '{"email":"admin@demo.local","password":"Demo-Passw0rd!"}'
```

Then open http://localhost:3000 (status page) and http://localhost:8080/swagger (API reference).

See [Developer Setup](docs/dev-setup.md) for prerequisites, Podman specifics, and running against a remote engine.

Once the Ingestion plan lands, the first event is:

```bash
curl -X POST http://localhost:8080/v1/track \
  -H "X-Api-Key: $CRM_API_KEY" -H "Content-Type: application/json" \
  -d '{"userId":"u-123","event":"purchase","properties":{"amount":49.9}}'
```

## Observability

```bash
make up-observability    # the stack plus Prometheus, Grafana and Jaeger
```

| Tool | URL | What is there |
|---|---|---|
| Grafana | http://localhost:3001 | dashboard "CRM overview": HTTP rate and latency now; ingest rate, consumer lag, DLQ and segment-run panels fill in as the modules land |
| Prometheus | http://localhost:9090 | scrapes `api:8080/metrics` and `worker:8081/metrics` |
| Jaeger | http://localhost:16686 | traces from `Crm.Api` and `Crm.Worker` (OTLP) |

## Tests

```bash
make test-unit           # unit, architecture, contract tests — seconds, no containers
make test-integration    # Testcontainers suite: SQL Server, Kafka, Redis
make e2e                 # Playwright against the running compose stack
make compose-check docs-check
```

## Measured results

| Case | make up | budget | make seed | budget | probe | verdict | date | machine |
|---|---|---|---|---|---|---|---|---|
<LT-005 ROW FROM TASK 8, VERBATIM>

LT-005 is recorded at foundation scope (`tests/Crm.LoadTests/coldstart.sh`); throughput results (LT-001…LT-003) are added by the load-test plan.

Minimum local resources: 8 GB RAM allocated to the container engine.
````

`<LT-005 ROW FROM TASK 8, VERBATIM>` is the one substitution in this plan: paste the row the controller gave you.

- [ ] **Step 2: `docs/dev-setup.md`**

1. After the "Remote engine (ssh tunnel)" bullets, add a bullet:

```markdown
- Pass overrides on the **make command line** (`make up MSSQL_HOST_PORT=11433`), not as environment
  variables. The Makefile includes `deploy/.env`, and a value assigned there replaces — and is
  re-exported over — an environment variable of the same name; only a command-line variable wins.
```

2. Add a section before "Everyday commands":

```markdown
## Observability

`make up-observability` starts the stack together with `deploy/docker-compose.observability.yml`
(profile `observability`): Prometheus on 9090, Grafana on 3001 (3000 belongs to the web shell), Jaeger
on 16686. `api` and `worker` get `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317`; without the profile
the variable is empty and OTLP export is off, while `/metrics` is always served.

Prometheus and Grafana configuration is baked into small images built from `deploy/observability/`,
not bind-mounted, so it also works when the engine is remote. After editing `prometheus.yml`, the
datasource, or `dashboards/crm-overview.json`, run `make up-observability` again to rebuild.

Host ports are configurable in `deploy/.env` (`PROMETHEUS_HOST_PORT`, `GRAFANA_HOST_PORT`,
`JAEGER_HOST_PORT`). With a remote engine use `make tunnel-observability`, which forwards the three
ports in addition to everything `make tunnel-apps` forwards. `make down` always removes the
observability containers too.
```

3. In the "Everyday commands" table: change the `make logs` row to `| \`make logs\` | follow api and worker logs; \`make logs SERVICES="kafka redis"\` picks others, \`SERVICES=\` follows everything |`, and add rows:

```markdown
| `make up-observability` | the stack plus Prometheus, Grafana, Jaeger |
| `make tunnel-observability` | like `tunnel-apps`, also forwards 9090, 3001, 16686 |
| `make compose-check` | CT-005 for both compose files |
| `tests/Crm.LoadTests/coldstart.sh` | LT-005: times `make up` + `make seed` from a stopped stack and runs the quickstart probe |
```

- [ ] **Step 3: Spec amendments (`docs/superpowers/specs/2026-09-16-foundation-design.md`)**

1. Replace the body of §8.2 with:

```markdown
Second compose file, profile `observability`, started together with the base file by `make up-observability`: `prometheus` (scrapes `api:8080/metrics`, `worker:8081/metrics`), `grafana` (provisioned Prometheus datasource, one dashboard `crm-overview` with HTTP rate/latency plus ingest rate, consumer lag, DLQ, segment-run and real-time-evaluation panels — the `crm_*` panels show "no data" until later plans), `jaeger` (v2 image, OTLP gRPC receiver on 4317, UI on 16686). The file overrides `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317` on `api` and `worker`.

Rulings (plan C): configuration is baked into images built from `deploy/observability/{prometheus,grafana}` — no bind mounts, because with a remote engine a bind path resolves on the engine host (C1). Host ports are `${PROMETHEUS_HOST_PORT:-9090}`, `${GRAFANA_HOST_PORT:-3001}` (web owns 3000), `${JAEGER_HOST_PORT:-16686}`; 4317 is not published (C2). `make tunnel-observability` forwards them from a remote engine. `make down` and `make ps` always include this file so its containers are never orphaned (C7). Grafana allows anonymous Viewer access — local development only.
```

2. In the §8.3 table: change the `logs` row's "Does" to `follow logs of api and worker (\`SERVICES="…"\` selects others)`, and add rows `| \`up-observability\` | base + observability files with \`--profile observability\`, \`up -d --build --wait\` |` and `| \`compose-check\` | renders both compose configurations and runs \`tools/compose_check.py\` (CT-005), the second with \`--observability\` |`.
3. §9 item 3: append `; \`python3 -m unittest discover -s tools/tests\` (compose_check, observability assets, Testcontainers image pins)`.
4. §11 plan C row: exit gate becomes `fresh-clone walkthrough within the LT-005 budgets at foundation scope on the home-lab Podman engine (\`tests/Crm.LoadTests/coldstart.sh\`); full LT-005 needs \`/v1/track\` and the seed volume from later plans`.
5. §13 "Package versions drift" row: mitigation becomes `central package management; weekly grouped Dependabot updates (\`.github/dependabot.yml\`)`.

- [ ] **Step 4: CT-005 note (`docs/test/architecture-contract.md`)**

Replace the CT-005 `- Implemented:` bullet with:

```markdown
- Implemented: `tools/compose_check.py` (unit-tested in `tools/tests/test_compose_check.py`), `make compose-check`, `.github/workflows/ci.yml` step `validate compose files`. `deploy/docker-compose.observability.yml` is validated by the same step with `--observability`: services `prometheus`, `grafana`, `jaeger`, their published container ports, healthchecks on `prometheus` and `grafana`, and `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317` on `api` and `worker`.
```

- [ ] **Step 5: Verify and commit**

Run: `make docs-check` → `0 errors`.
Run: `grep -n "LT-005 ROW" README.md` → no match (the substitution was made).

```bash
git add README.md docs/dev-setup.md docs/superpowers/specs/2026-09-16-foundation-design.md docs/test/architecture-contract.md
git commit -m "docs: final developer setup, observability quickstart, measured LT-005, and spec rulings for plan C

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Exit gate

1. `dotnet build Crm.sln -warnaserror` → 0 warnings.
2. `make test-unit` green; integration suite green (24, 0 skipped) on the compose images.
3. `python3 -m unittest discover -s tools/tests -v` green; `make compose-check` prints two `OK` lines; `make docs-check` `0 errors`.
4. Web: `npm run lint`, `npm run typecheck`, `npm test -- --run`, `make e2e` green.
5. Task 8 checks all held; `coldstart.sh` exited 0 (stack stopped, volumes removed, image layers cached — see ledger ruling; spec §11 amended); its row is in the README.
6. Final whole-branch review (Opus), one fix wave, user pushes, CI green.

## Self-review notes

- **Spec coverage:** §8.2 → Task 3 (+ Task 8 live check); §8.3 `logs` → Task 3 Step 7; §9 observability compose validation → Tasks 2–3; §7.3 `input`/`card` → Task 5; §11 plan C deliverables: observability profile + Grafana dashboard → Task 3, README/dev-setup final → Task 9, LT-005 recorded → Tasks 7–9; §13 Dependabot → Task 6. Carried minors: data dictionary order, image literals, test-id labelling → Task 4; `make logs` filter → Task 3; tsconfig.node typecheck → Task 5.
- **Deliberately not done:** IT-040 (needs the pipeline), `crm_*` meters (owned by the module plans), RS256, Grafana alerting, log aggregation (Loki) — not in the spec. AT-006 depth grows by itself as module entities arrive. Plan B's dropped minors stay dropped per its final review.
- **Type/name consistency:** `check(config, observability)` and the constant `http://jaeger:4317` are identical in Tasks 2, 3 and 9; ports 9090 / 3001 / 16686 identical in compose, Makefile, tunnel, README, dev-setup; dashboard uid `crm-overview` and datasource uid `prometheus` identical in JSON, provisioning, tests and Task 8 checks; `CardTitle` `as` prop used the same way in both routes and the test.

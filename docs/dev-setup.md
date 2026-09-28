# Developer Setup

How to run the CRM platform locally. Verified on macOS (Apple Silicon) with Podman and with a remote
Podman host reached over ssh; the same commands work with Docker Desktop.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.400 (pinned in `global.json`) | `dotnet --version` |
| Node | 22 LTS or newer | 26 works locally; the web image builds with 22 |
| Container engine | Docker Desktop, a local Podman machine, or a remote Podman host over ssh (see Remote engine) | `docker context ls` shows which is active |
| docker-compose | standalone v2/v5 binary, or the `docker compose` plugin | the Makefile detects whichever is installed and uses it |
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

- The SQL Server 2022 image is amd64-only and segfaults under QEMU emulation on the libkrun Podman machine (no Rosetta). On arm64 machines without Rosetta set `MSSQL_IMAGE=mcr.microsoft.com/azure-sql-edge:latest`, an arm64-native engine with the same T-SQL/JSON surface slice 1 uses (the example env now defaults to SQL Server 2022). CI runs SQL Server 2022; if a query works on Edge but fails in CI, CI wins.
- Redis is published on host port 6380 (`REDIS_HOST_PORT`) because 6379 is frequently taken by other local projects.
- Testcontainers needs the engine socket and no Ryuk: `make test-integration` uses `DOCKER_HOST` when set (e.g. the ssh tunnel socket), else the active docker context; it also sets `TESTCONTAINERS_RYUK_DISABLED=true` and `MSSQL_IMAGE` from `deploy/.env`. Run it directly with those variables set if you bypass the Makefile.
- Port conflicts show up as `proxy already running` from the Podman API. Check `docker ps -a --format '{{.Names}} {{.Ports}}'` for the container holding the port and change the host port in `deploy/.env`.
- `NU1301 … api.nuget.org … timed out after 100000ms` during `make up`, for many minutes, means the engine host resolves nuget.org to an IPv6 address but has a dead IPv6 route. Fix with `make up DOTNET_SYSTEM_NET_DISABLEIPV6=1`, or set `DOTNET_SYSTEM_NET_DISABLEIPV6=1` in `deploy/.env`; it is a build argument of the api and worker images and affects the build stage only. Changing the value rebuilds the restore layer once.
- On a fresh `sqldata` volume, the API creates the Development database itself and switches `READ_COMMITTED_SNAPSHOT` on `WITH ROLLBACK IMMEDIATE` before running its first migration, instead of letting EF Core's own database creator do it: the Worker starts in parallel by design and only waits for the schema, so it may already hold a session in the brand-new database, and EF Core's unguarded `ALTER DATABASE` would otherwise wait for that session until its 60 s command timeout and crash the API (ruling F18).

## Remote engine (ssh tunnel)

When the container engine runs on another machine (a home-lab box running Podman) instead of locally:

- `make tunnel` opens an ssh tunnel forwarding the engine's Podman API socket to `$(ENGINE_SOCK)` and maps
  the stack's infrastructure ports back to localhost: SQL Server (`1433` → `MSSQL_HOST_PORT`), Redis
  (`6380`), Kafka (`19092`).
- `make tunnel-apps` does the same plus forwards the app ports 8080 (api), 8081 (worker), 3000 (web) —
  use it only when those apps run in compose on the remote engine, never while also running hosts
  locally with `dotnet run` (it would shadow the local listeners).
- Point Docker at the tunnel socket, either with a docker context:

  ```bash
  docker context create home-lab --docker host=unix://$HOME/.local/share/containers/home-lab.sock
  docker context use home-lab
  ```

  or per-command with `DOCKER_HOST=unix://$HOME/.local/share/containers/home-lab.sock`.
- Set in `deploy/.env`:
  - `MSSQL_HOST_PORT=11433` when the remote engine already runs its own SQL Server on 1433.
  - `TESTCONTAINERS_HOST_OVERRIDE=<host>` so Testcontainers' randomly mapped ports are reachable from
    your workstation by that hostname rather than `localhost`.
- Use `ENGINE_SSH_HOST` and `ENGINE_REMOTE_SOCK` to point the Makefile at a different host or a
  differently located remote socket.
- Pass overrides on the **make command line** (`make up MSSQL_HOST_PORT=11433`), not as environment
  variables. The Makefile includes `deploy/.env`, and a value assigned there replaces — and is
  re-exported over — an environment variable of the same name; only a command-line variable wins.
- The tunnel is a plain ssh process; if the workstation sleeps it dies and compose fails with
  `Cannot connect to the Docker daemon` / `unexpected EOF` — rerun `make tunnel` (or the variant you
  use); for long runs wrap the command in `caffeinate -i` on macOS.

## Observability

`make up-observability` starts the stack together with `deploy/docker-compose.observability.yml`
(profile `observability`): Prometheus on 9090, Grafana on 3001 (3000 belongs to the web shell), Jaeger
on 16686. `api` and `worker` get `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317`; without the profile
the variable is empty and OTLP export is off, while `/metrics` is always served.

Prometheus and Grafana configuration is baked into small images built from `deploy/observability/`,
not bind-mounted, so it also works when the engine is remote. After editing `prometheus.yml`, the
datasource, or `dashboards/crm-overview.json`, run `make up-observability` again to rebuild.

Host ports are configurable in `deploy/.env` (`PROMETHEUS_HOST_PORT`, `GRAFANA_HOST_PORT`,
`JAEGER_HOST_PORT`). All three publish on the engine's loopback only (`127.0.0.1:<port>:<container
port>`) — anonymous Grafana and unauthenticated Prometheus/Jaeger must not face the LAN on a remote
engine; the ssh tunnel's `-L …:localhost:PORT` still reaches them because `localhost` there resolves
on the engine, not the workstation. With a remote engine use `make tunnel-observability`, which
forwards the three ports in addition to everything `make tunnel-apps` forwards. `make down` always
removes the observability containers too.

Port 9090 is often taken on Linux hosts (Cockpit listens there on Rocky/RHEL/Fedora); the engine then
fails with `rootlessport listen tcp 0.0.0.0:9090: bind: address already in use`. Use e.g.
`PROMETHEUS_HOST_PORT=19090` on BOTH `make tunnel-observability` and `make up-observability` (command
line).

Requests to `/health/*` and `/metrics` are deliberately excluded from tracing, so Jaeger lists
`Crm.Api` only after a `/v1/...` request.

## Everyday commands

| Command | Purpose |
|---|---|
| `make test-unit` | unit, architecture, contract tests (seconds, no containers) |
| `make test-integration` | Testcontainers suite (minutes; first run pulls images) |
| `make test` | both |
| `make docs-check` | documentation checker |
| `make migrate` | apply EF Core migrations (module contexts with migrations: Tenancy, Profiles) against `localhost,1433` |
| `make seed` | bootstrap the demo tenant, Admin + Viewer users, and an API key against a running Development API; writes `CRM_API_KEY` to `.env.local` (git-ignored); idempotent; a stored key that is no longer an active key of the tenant (database recreated with `make down`, or key revoked) is replaced. It then logs in as that Admin and calls the Development-only `POST /v1/dev/seed-dataset` (404 outside Development) to bulk-load the 5k-profile / 100k-event demo dataset with `SqlBulkCopy`; the endpoint self-verifies the six documented segment counts and rolls back on a mismatch. Idempotent: a tenant that already has it reports `exists` without reloading. It then calls the Development-only `POST /v1/dev/seed-segments` (H22) to upsert the six demo segments (`seed-pro-id`, `seed-purchasers`, `seed-dormant`, `seed-no-plan`, `seed-not-pro`, `seed-high-ltv-sg`) and queue a run for each Active one, then polls `GET /v1/segments` (timeout 120 s) until the five Active ones finish and compares their `memberCount` against the dataset response's own counts. `seed-high-ltv-sg` is `Draft` and is never queued, so it is checked separately: it must stay unmaterialised (`memberCount` 0, no `lastRunAt`) and is printed as `draft — not materialised (rule would match N)`, never compared against the rule's match count. Idempotent, since an existing segment is left untouched and an already-run one is not re-queued. Typical duration in this repo's tests: ~10 s for the dataset load, measured against SQL Server on a remote engine over the LAN (`DOCKER_HOST`/`TESTCONTAINERS_HOST_OVERRIDE`); a local engine should be at least as fast; segment seeding adds a few more seconds (LT-005 re-measured in a later task) |
| `make openapi` | regenerate `docs/api/openapi.json` (commit the result). `dotnet run --project <dir>` resolves a relative `--export-openapi` path against the *project* directory, not your shell's working directory, so this target (and CI) pass an absolute path |
| `make web-dev` | Vite dev server on 5173 proxying `/v1` and `/health` to `localhost:8080` |
| `make tunnel` | ssh tunnel to a remote engine: podman socket + infrastructure ports |
| `make tunnel-apps` | like `tunnel`, also forwards api/worker/web ports for a compose stack running remotely |
| `make e2e` | Playwright against the compose stack |
| `make logs` | follow api and worker logs; `make logs SERVICES="kafka redis"` picks others, `SERVICES=` follows everything |
| `make down` | stop and delete volumes (`KEEP=1 make down` keeps data) |
| `make up-observability` | the stack plus Prometheus, Grafana, Jaeger |
| `make tunnel-observability` | like `tunnel-apps`, also forwards 9090, 3001, 16686 |
| `make compose-check` | CT-005 for both compose files |
| `tests/Crm.LoadTests/coldstart.sh` | LT-005: times `make up` + `make seed` from a stopped stack and runs the quickstart probe |

`make seed`'s dataset load verifies a **delta**, not an absolute count: it runs into a tenant that already has profiles (from earlier manual testing, for example) without failing, because it checks what *this* load added against `docs/test/fixtures.md`, not the tenant's total. The load still briefly blocks all writers on `profiles.*` — the single transaction's cumulative lock footprint escalates `profiles.Events` to a table lock for the ~10 s it runs — so do not run it against a shared or busy database. The seed endpoint (`POST /v1/dev/seed-dataset`) exists only when `ASPNETCORE_ENVIRONMENT=Development`; never run an exposed host in that environment (it also maps tenant bootstrap and auto-applies migrations). It is Admin-only and loads only into the caller's own tenant. `POST /v1/dev/seed-segments` is the same shape — Development-only, Admin-only, caller's tenant only, excluded from OpenAPI — and upserts by segment name, so a segment a user has since edited is never overwritten by a later `make seed`.

## Profiles Worker

The `events.raw` consumer waits for schema `profiles` to exist before subscribing (`SchemaGate` polls every 2 s, ruling E11) and logs `Waiting for schema 'profiles' (applied by the API in Development, or by \`make migrate\`)` until it does. In compose the API applies migrations in Development, so a Worker that starts first is expected to log this for a few seconds.

Configuration keys (`Profiles:Consumer` section):

| Key | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | `false` keeps the services registered but never subscribes |
| `GroupId` | `profiles` | Kafka consumer group |
| `Instances` | `4` | consumers run in this process; Kafka spreads the 12 partitions over them (ruling E10) |
| `RetryDelaysMs` | `[200, 2000]` | waits between attempts before dead-lettering (ruling E8) |
| `RestartDelayMs` | `1000` | pause before a consumer is rebuilt after a failure outside message processing (ruling E9) |

Dead letters land on `events.dlq` with `error`, `stack`, and `attempt` headers; inspect the last few with:

```bash
docker exec crm-kafka-1 /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic events.dlq --from-beginning --property print.headers=true --max-messages 5
```

## Segmentation Worker

`SegmentRunWorker` drains queued batch runs; `SegmentScheduler` is a separate `BackgroundService` that queues a Schedule run for every Active segment on its own interval (H3). Both wait for schema `segments` to exist the same way the Profiles consumer waits for `profiles` (`SegmentSchemaGate`).

Configuration keys (`Segmentation:Run` / `Segmentation:Schedule` sections):

| Key | Default | Purpose |
|---|---|---|
| `Segmentation:Run:CommandTimeoutSeconds` | `300` | per-statement SQL command budget for a run's transaction (H6); `≤ 0` (0 means "no timeout" to ADO.NET, a negative throws) falls back to the default with a startup warning (I-4) |
| `Segmentation:Run:PollInterval` | `00:00:05` | how often `SegmentRunWorker` drains queued runs; `≤ 0` falls back to the default with a startup warning |
| `Segmentation:Schedule:Interval` | `06:00:00` | how often `SegmentScheduler` sweeps for Active segments with no active run; `≤ 0` would otherwise crash `PeriodicTimer` and bring down the whole Worker host — falls back to the default with a startup warning (I-4) |

A run stuck `Running` past the reap window is failed as `'abandoned'` and can be re-queued; the window is `max(10 min, 8 × Segmentation:Run:CommandTimeoutSeconds)`, not a flat 10 minutes — a run issues about eight statements inside that per-statement budget, so its wall-clock ceiling scales with it (H29).

## Logging in

After `make seed`, log in as the seeded Admin and call an authenticated endpoint:

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@demo.local","password":"Demo-Passw0rd!"}' | jq -r .accessToken)
curl -s http://localhost:8080/v1/api-keys -H "Authorization: Bearer $TOKEN"
```

Or send the first event with the API key `make seed` wrote to `.env.local`:

```bash
source .env.local && curl -X POST http://localhost:8080/v1/track \
  -H "X-Api-Key: $CRM_API_KEY" -H "Content-Type: application/json" \
  -d '{"userId":"u-123","event":"purchase","properties":{"amount":49.9}}'
```

## Running hosts from the IDE

`dotnet run --project src/Hosts/Crm.Api` and `dotnet run --project src/Hosts/Crm.Worker` use `appsettings.Development.json`, which points at the compose ports (`localhost,1433`, `localhost:6380`, `localhost:19092`). Stop the compose `api`/`worker` containers first to free ports 8080/8081, or run with `--urls http://localhost:8090`.

## Web dashboard

`src/Hosts/Crm.Web/src/lib/api.generated.ts` is generated from the committed `docs/api/openapi.json` by `openapi-typescript` and committed itself. After changing the API's OpenAPI document, regenerate it from `src/Hosts/Crm.Web`:

```bash
npm run api:types
```

CI's web job runs the same command and fails the build (`git diff --exit-code`) if the committed file has drifted from the OpenAPI document (plan J, ruling J3).

### Running the E2E suite

Playwright specs (`src/Hosts/Crm.Web/e2e/`) run against a live stack, not a mock — bring one up first (Prerequisite):

```bash
make up && make seed          # or, with a remote engine: make tunnel-apps first
cd src/Hosts/Crm.Web
npm run dev                   # Vite dev server on 5173, proxying /v1 and /health to :8080
```

Then, in another shell:

```bash
cd src/Hosts/Crm.Web
npx playwright install --with-deps chromium   # first run only
E2E_BASE_URL=http://localhost:5173 npx playwright test
```

`E2E_BASE_URL` overrides the default `http://localhost:3000` (the built `web` container's port) so the suite runs against the Vite dev server instead. The suite runs with a single worker (`playwright.config.ts`): `/v1/auth/login` and `/v1/auth/refresh` share a 20-request-per-minute rate limit (ruling J19), so parallel workers would trip it.

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

## Everyday commands

| Command | Purpose |
|---|---|
| `make test-unit` | unit, architecture, contract tests (seconds, no containers) |
| `make test-integration` | Testcontainers suite (minutes; first run pulls images) |
| `make test` | both |
| `make docs-check` | documentation checker |
| `make migrate` | apply EF Core migrations (module contexts with migrations: Tenancy) against `localhost,1433` |
| `make seed` | bootstrap the demo tenant, Admin + Viewer users, and an API key against a running Development API; writes `CRM_API_KEY` to `.env.local` (git-ignored); idempotent; a stored key that is no longer an active key of the tenant (database recreated with `make down`, or key revoked) is replaced |
| `make openapi` | regenerate `docs/api/openapi.json` (commit the result). `dotnet run --project <dir>` resolves a relative `--export-openapi` path against the *project* directory, not your shell's working directory, so this target (and CI) pass an absolute path |
| `make web-dev` | Vite dev server on 5173 proxying `/v1` and `/health` to `localhost:8080` |
| `make tunnel` | ssh tunnel to a remote engine: podman socket + infrastructure ports |
| `make tunnel-apps` | like `tunnel`, also forwards api/worker/web ports for a compose stack running remotely |
| `make e2e` | Playwright against the compose stack |
| `make logs` | follow api and worker logs |
| `make down` | stop and delete volumes (`KEEP=1 make down` keeps data) |

## Logging in

After `make seed`, log in as the seeded Admin and call an authenticated endpoint:

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@demo.local","password":"Demo-Passw0rd!"}' | jq -r .accessToken)
curl -s http://localhost:8080/v1/api-keys -H "Authorization: Bearer $TOKEN"
```

## Running hosts from the IDE

`dotnet run --project src/Hosts/Crm.Api` and `dotnet run --project src/Hosts/Crm.Worker` use `appsettings.Development.json`, which points at the compose ports (`localhost,1433`, `localhost:6380`, `localhost:19092`). Stop the compose `api`/`worker` containers first to free ports 8080/8081, or run with `--urls http://localhost:8090`.

# CRM Platform

![ci](https://github.com/fef-ai-ml/crm/actions/workflows/ci.yml/badge.svg)

A MoEngage-style customer engagement platform built as a portfolio project: high-throughput event ingestion, identity resolution into unified customer profiles, and a segmentation engine that evaluates rules in batch and in real time.

Stack: .NET 10 modular monolith (API + Worker), React 19 dashboard, SQL Server 2022, Kafka (KRaft), Redis 7, docker-compose.

## Status

Slice 1 (events → profiles → segments) has its foundation complete: solution structure, Kernel, Contracts, the API/Worker/Web hosts, multi-tenant auth, CI, and an opt-in observability stack. The Ingestion API is also complete, and the Profiles pipeline's write side (Worker consumer, identity resolution, merge, DLQ) now turns those events into profiles; profiles are now readable through `GET /v1/profiles`, `GET /v1/profiles/{id}` and `GET /v1/profiles/{id}/events`. Segmentation lands next, behind its own plan under `docs/superpowers/plans/`.

| Phase | State |
|---|---|
| Design spec | Approved |
| Documentation (BRD, ADRs, architecture, data dictionary, API reference) | Complete |
| Implementation: solution skeleton, Kernel, Contracts, hosts, web shell, CI | Complete |
| Implementation: Tenancy | Complete |
| Implementation: developer experience (observability profile, Dependabot, cold-start measurement) | Complete |
| Implementation: Ingestion API (track, identify, batch → Kafka) | Complete |
| Implementation: Profiles pipeline — write side (Worker consumer, identity resolution, merge, DLQ) | Complete |
| Implementation: Profiles read API | Complete |
| Implementation: Segmentation | Planned |
| Implementation: Web dashboard | Planned |
| Load test and results | Planned |

## Documentation

| Document | What it answers |
|---|---|
| [Documentation index](docs/README.md) | Where everything is |
| [Design spec](docs/superpowers/specs/2026-09-15-crm-slice1-design.md) | What slice 1 builds and how |
| [Business requirements](docs/brd/BRD.md) | Why, for whom, and what "done" means (FR/NFR ids) |
| [Architecture](docs/design/architecture.md) | C4 diagrams, module map, sequence diagrams |
| [Architecture decisions](docs/adr/README.md) | Why the monolith, Kafka, SQL Server JSON, shared-DB tenancy, hybrid segmentation, Redis roles, auth, run-and-swap, cross-schema reads |
| [Data dictionary](docs/data-dictionary/README.md) | Tables, columns, indexes, Redis keys, Kafka topics |
| [API reference](docs/api/README.md) | Endpoints, auth, errors, pagination, examples |
| [Test specification](docs/test/README.md) | 125 test cases at six levels, traceability to every FR/NFR, fixtures |
| [Developer setup](docs/dev-setup.md) | Prerequisites, first run, Podman specifics, everyday commands |

Validate the documentation set:

```bash
python3 tools/docs_check.py
```

## Quickstart

Three commands from a fresh clone:

```bash
make up          # infrastructure + api + worker + web, builds images, waits until healthy
make seed        # demo tenant, users, API key, 5k profiles / 100k events
source .env.local && curl -X POST http://localhost:8080/v1/track \
  -H "X-Api-Key: $CRM_API_KEY" -H "Content-Type: application/json" \
  -d '{"userId":"u-123","event":"purchase","properties":{"amount":49.9}}'    # 202 {"eventId":"…"}
```

Then open http://localhost:3000 (status page) and http://localhost:8080/swagger (API reference).

See [Developer Setup](docs/dev-setup.md) for prerequisites, Podman specifics, and running against a remote engine.

The event is now on Kafka topic `events.raw`; the Worker resolves the profile, appends the event, and publishes `EventTracked` to `events.processed`. Read it back through the profile read API after a moment — the Worker builds the profile asynchronously:

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/v1/auth/login -H "Content-Type: application/json" -d '{"email":"admin@demo.local","password":"Demo-Passw0rd!"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["accessToken"])')
curl -s "http://localhost:8080/v1/profiles?q=u-123" -H "Authorization: Bearer $TOKEN"    # the profile the curl above created
```

The Web dashboard comes later.

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
| LT-005 (foundation scope) | 52 s | 180 s | 2 s | 120 s | 202 | pass | 2026-09-20 | home-lab: Rocky 10.2, 4 vCPU, 15 GB, Podman 5.8 (remote over ssh); image layers cached |

LT-005 is recorded at foundation scope with `tests/Crm.LoadTests/coldstart.sh`: stack stopped, volumes removed, image layers cached; the probe is the quickstart's first event (`POST /v1/track` → 202). A first build on a machine without cached layers additionally pays the NuGet restore and publish inside the image build. Throughput results (LT-001…LT-003) are added by the load-test plan.

Minimum local resources: 8 GB RAM allocated to the container engine.

## Roadmap

1. Slice 1: events, profiles, segments (this repository's current focus)
2. Campaigns: email channel, templates, send-to-segment
3. Journeys / Flows
4. Analytics: funnels, retention
5. Channels and SDKs: push, SMS, WhatsApp, web SDK
6. AI: send-time optimization, churn scoring

Each slice gets its own design spec and implementation plan under `docs/superpowers/`.

## License

Apache-2.0. See [LICENSE](LICENSE).

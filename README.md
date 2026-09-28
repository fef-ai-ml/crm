# CRM Platform

![ci](https://github.com/fef-ai-ml/crm/actions/workflows/ci.yml/badge.svg)

A MoEngage-style customer engagement platform built as a portfolio project: high-throughput event ingestion, identity resolution into unified customer profiles, and a segmentation engine that evaluates rules in batch and in real time.

Stack: .NET 10 modular monolith (API + Worker), React 19 dashboard, SQL Server 2022, Kafka (KRaft), Redis 7, docker-compose.

## Status

Slice 1 (events → profiles → segments) has its foundation complete: solution structure, Kernel, Contracts, the API/Worker/Web hosts, multi-tenant auth, CI, and an opt-in observability stack. The Ingestion API is also complete, and the Profiles pipeline's write side (Worker consumer, identity resolution, merge, DLQ) now turns those events into profiles; profiles are now readable through `GET /v1/profiles`, `GET /v1/profiles/{id}` and `GET /v1/profiles/{id}/events`. Segmentation's rule engine, batch materialization and API are complete: segments are defined by a validated rule tree, compiled to parameterized SQL, previewed in under five seconds, materialized by queued batch runs on the Worker (manual and six-hourly), and read back as members and membership changes through `/v1/segments`. Real-time evaluation keeps a Redis membership set for every Active, real-time-eligible segment: the Worker's `events.processed` consumer updates event counters and re-evaluates the profile on every event or identify, records each transition as a `Realtime` membership change, and exposes the set's size as `realtimeMemberCount`; every batch run rebuilds the set. The Web dashboard is also complete: login, a segment builder with a live member count and server-validated rules, run/members/runs on each segment, a profile explorer with search and an event timeline, and an Admin-only API keys screen, with Viewer sessions rendered read-only throughout.

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
| Implementation: Segmentation (rules, batch, API) | Complete |
| Implementation: Segmentation (real-time evaluation) | Complete (LT-003 passes idle; under load it is bounded by the profiles pipeline — see below) |
| Implementation: Web dashboard | Complete |
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
| [Test specification](docs/test/README.md) | 126 test cases at six levels, traceability to every FR/NFR, fixtures |
| [Developer setup](docs/dev-setup.md) | Prerequisites, first run, Podman specifics, everyday commands |

Validate the documentation set:

```bash
python3 tools/docs_check.py
```

## Quickstart

Three commands from a fresh clone:

```bash
make up          # infrastructure + api + worker + web, builds images, waits until healthy
make seed        # demo tenant, users, API key, 5k profiles / 100k events, 6 demo segments
source .env.local && curl -X POST http://localhost:8080/v1/track \
  -H "X-Api-Key: $CRM_API_KEY" -H "Content-Type: application/json" \
  -d '{"userId":"u-123","event":"purchase","properties":{"amount":49.9}}'    # 202 {"eventId":"…"}
```

Then open http://localhost:3000 and log in as admin@demo.local / Demo-Passw0rd! to reach the dashboard, or http://localhost:8080/swagger for the API reference.

See [Developer Setup](docs/dev-setup.md) for prerequisites, Podman specifics, and running against a remote engine.

The event is now on Kafka topic `events.raw`; the Worker resolves the profile, appends the event, and publishes `EventTracked` to `events.processed`. Read it back through the profile read API after a moment — the Worker builds the profile asynchronously:

```bash
TOKEN=$(curl -s -X POST http://localhost:8080/v1/auth/login -H "Content-Type: application/json" -d '{"email":"admin@demo.local","password":"Demo-Passw0rd!"}' | python3 -c 'import json,sys;print(json.load(sys.stdin)["accessToken"])')
curl -s "http://localhost:8080/v1/profiles?q=u-123" -H "Authorization: Bearer $TOKEN"    # the profile the curl above created
```

Or see the same profile in the dashboard's profile explorer at http://localhost:3000/profiles.

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
| LT-005 | 74 s | 180 s | 13 s | 120 s | 202 | pass | 2026-09-21 | home-lab: Rocky 10.2, 4 vCPU, 15 GB, Podman 5.8 (remote over ssh); image layers cached |

LT-005 is measured with `tests/Crm.LoadTests/coldstart.sh`: stack stopped, volumes removed, image layers cached. `make seed` includes loading and self-verifying the demo dataset (5,000 profiles, 100,000 events — about 10 s of the 13 s); the probe is the quickstart's first event (`POST /v1/track` → 202). With the api and worker images rebuilt after a code change (restore and publish inside the image build, base layers cached) the same run measured `make up` at 171 s; a machine without any cached layers pays more. Throughput results (LT-001…LT-003) are added by the load-test plan. A first LT-003 run (plan I, 2026-09-27, cloud dev container, 4 vCPU, apps on the host rather than in images) measured real-time segment admission at p95 60 ms on an idle stack, but p95 8.2 s under an ad-hoc 1,000 events/s load: the profiles consumer fell behind on `events.raw` (≈320 events/s drain rate on that box) while the segmentation consumer stayed current — the load-test plan's first target.

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

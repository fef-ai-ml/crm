# CRM Platform

A MoEngage-style customer engagement platform built as a portfolio project: high-throughput event ingestion, identity resolution into unified customer profiles, and a segmentation engine that evaluates rules in batch and in real time.

Stack: .NET 10 modular monolith (API + Worker), React 19 dashboard, SQL Server 2022, Kafka (KRaft), Redis 7, docker-compose.

## Status

Slice 1 (events → profiles → segments) is in the documentation phase. The design is approved and every document below exists and is validated; implementation follows the plans under `docs/superpowers/plans/`.

| Phase | State |
|---|---|
| Design spec | Approved |
| Documentation (BRD, ADRs, architecture, data dictionary, API reference) | Complete |
| Implementation: Kernel, Contracts, Tenancy | Planned |
| Implementation: Ingestion, Profiles pipeline | Planned |
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

Validate the documentation set:

```bash
python3 tools/docs_check.py
```

## Quickstart

The commands below are the target developer experience for slice 1 and become available as the implementation plans land. Until then they are the contract the implementation must satisfy.

```bash
make up                                               # docker compose -f deploy/docker-compose.yml up -d: SQL Server, Kafka, Redis, API, Worker, Web
make seed                                             # demo tenant, admin user, API key, 5k profiles, 100k events
open http://localhost:3000                            # dashboard (nginx-served build); API at http://localhost:8080, Swagger at /swagger
```

Send a first event:

```bash
curl -X POST http://localhost:8080/v1/track \
  -H "X-Api-Key: $CRM_API_KEY" -H "Content-Type: application/json" \
  -d '{"userId":"u-123","event":"purchase","properties":{"amount":49.9}}'
```

Run the tests:

```bash
dotnet test                 # unit, integration (Testcontainers), architecture
cd src/Hosts/Crm.Web && npm test && npx playwright test
k6 run tests/Crm.LoadTests/ingest.js
```

Minimum local resources: 8 GB RAM allocated to Docker.

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

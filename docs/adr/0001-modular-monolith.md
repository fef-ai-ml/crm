# 0001. Build the platform as a modular monolith

- **Status:** Accepted
- **Date:** 2026-09-15
- **Deciders:** project owner

## Context and Problem Statement

The platform has at least four clear bounded contexts in slice 1 (Ingestion, Profiles, Segmentation, Tenancy) and more later (Campaigns, Journeys, Analytics). The project is a portfolio and learning effort run by one person on one machine. How should the code be partitioned so that boundaries are real and reviewable without paying the operational cost of distributed services?

## Decision Drivers

- NFR-08: module dependency rules must be enforced automatically.
- NFR-10: everything must run with `docker compose` on a developer machine.
- Portfolio value: reviewers should see disciplined boundaries and a credible path to extraction.
- One developer: minimize infrastructure and deployment surface.

## Considered Options

1. Modular monolith: one solution, one database with a schema per module, separate projects per module, two runtime hosts (API, Worker).
2. Microservices from day one: one service per bounded context, each with its own database and container, communicating over Kafka and HTTP.
3. Layered single application (Clean Architecture without module boundaries): Domain, Application, Infrastructure, API projects shared by all features.

## Decision Outcome

Chosen option: "Modular monolith", because it gives most of the isolation benefit of services (own schema, own DbContext, own migrations, no cross-module assembly references) at zero extra infrastructure, and the boundaries are enforced by architecture tests rather than by network calls.

### Positive consequences

- One `docker compose up` runs everything; one CI pipeline.
- Cross-module contracts live in `Crm.Contracts`; a module can be extracted to a service later by moving its schema and swapping in-process calls for Kafka messages already in use.
- Refactoring across modules is a normal pull request, not a coordinated release.

### Negative consequences

- All modules scale together; a hot Segmentation batch competes with ingestion for CPU in the same Worker process.
- Discipline depends on tests: without `Crm.ArchTests` the boundaries would erode.

## Consequences

Module layout, dependency rules, and schema-per-module are specified in the design spec §2.3–2.4. `Crm.ArchTests` fails the build when a module references another module's assembly or a host. The one sanctioned cross-module data read is recorded in [0009](0009-segmentation-reads-profiles-tables.md).

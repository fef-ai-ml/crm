# CRM Documentation

Documentation for the MoEngage-style CRM platform. Slice 1 covers event ingestion, unified profiles, and segmentation. The design spec is the source of truth; every other document restates or expands it.

## Documents

| Document | Purpose |
|---|---|
| [Design spec (slice 1)](superpowers/specs/2026-09-15-crm-slice1-design.md) | Approved design: architecture, data model, pipeline, segmentation, API, UI, testing |
| [Business Requirements (BRD)](brd/BRD.md) | Vision, personas, scope, functional and non-functional requirements, roadmap |
| [Architecture](design/architecture.md) | C4 diagrams (context, container, component), module map, sequence diagrams |
| [Architecture Decision Records](adr/README.md) | Nine decisions in MADR format |
| [Data Dictionary](data-dictionary/README.md) | Tables, columns, indexes, Redis keys, Kafka topics |
| [API Reference](api/README.md) | Endpoints, authentication, errors, pagination, curl examples |
| [Test Specification](test/README.md) | 125 test cases across six levels, traceability matrix to every FR/NFR, fixtures and seed dataset |

## Deviations from spec §9

- No separate `docs/design/<date>-design.md` copy of the design spec; the [architecture doc](design/architecture.md) links to the [spec](superpowers/specs/2026-09-15-crm-slice1-design.md) instead of duplicating it.
- `docs/api/openapi.json` is produced by the implementation build (generated from code, committed at that point) and is not part of the checker manifest.

## Validation

Run `python3 tools/docs_check.py` from the repository root. It verifies every document above exists, has its required sections, has no broken relative links, and has well-formed Mermaid blocks.

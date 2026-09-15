#!/usr/bin/env python3
"""Validate the docs/ tree: required files, required headings, relative links, Mermaid fences.

Usage: python3 tools/docs_check.py [--only PATH_PREFIX]
Exit 0 when clean, 1 when any ERROR line was printed.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

MADR = [
    "## Context and Problem Statement",
    "## Decision Drivers",
    "## Considered Options",
    "## Decision Outcome",
    "## Consequences",
]

ADRS = [
    (1, "modular-monolith"),
    (2, "kafka-ingestion"),
    (3, "sql-server-json-profiles"),
    (4, "shared-db-tenancy"),
    (5, "hybrid-segmentation"),
    (6, "redis-roles"),
    (7, "auth-model"),
    (8, "run-and-swap-membership"),
    (9, "segmentation-reads-profiles-tables"),
]

REQUIRED: dict[str, list[str]] = {
    "README.md": ["# ", "## Status", "## Documentation", "## Quickstart"],
    "docs/README.md": ["# CRM Documentation", "## Documents"],
    "docs/brd/BRD.md": [
        "# ",
        "## 1. Vision",
        "## 2. Personas",
        "## 3. Scope",
        "## 4. Functional Requirements",
        "## 5. Non-Functional Requirements",
        "## 6. Out of Scope",
        "## 7. Roadmap",
        "## 8. Glossary",
    ],
    "docs/adr/README.md": ["# Architecture Decision Records", "## Index", "## Adding an ADR"],
    "docs/adr/template.md": ["# "] + MADR,
    "docs/design/README.md": ["# Design"],
    "docs/design/architecture.md": [
        "# ",
        "## 1. System Context",
        "## 2. Containers",
        "## 3. Components",
        "## 4. Module Map",
        "## 5. Sequence Diagrams",
    ],
    "docs/data-dictionary/README.md": ["# Data Dictionary", "## Conventions", "## Schemas"],
    "docs/data-dictionary/profiles.md": ["# Schema `profiles`", "## Profiles", "## UserIdentities", "## Events"],
    "docs/data-dictionary/segments.md": [
        "# Schema `segments`",
        "## Segments",
        "## SegmentRuns",
        "## SegmentRunMembers",
        "## MembershipChanges",
    ],
    "docs/data-dictionary/identity.md": ["# Schema `identity`", "## Tenants", "## ApiKeys", "## Users"],
    "docs/data-dictionary/redis-and-kafka.md": ["# Redis Keys and Kafka Topics", "## Redis", "## Kafka"],
    "docs/api/README.md": [
        "# API Reference",
        "## Authentication",
        "## Errors",
        "## Pagination",
        "## Ingest",
        "## Profiles",
        "## Segments",
        "## Tenancy",
        "## Ops",
    ],
}
for number, slug in ADRS:
    REQUIRED[f"docs/adr/{number:04d}-{slug}.md"] = ["# "] + MADR

LINK_RE = re.compile(r"\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")


def heading_present(lines: list[str], heading: str) -> bool:
    if heading == "# ":
        return any(line.startswith("# ") for line in lines)
    return any(line.rstrip() == heading for line in lines)


def check_headings(path: Path, text: str, headings: list[str]) -> list[str]:
    lines = text.splitlines()
    return [f"missing heading '{h}'" for h in headings if not heading_present(lines, h)]


def check_links(path: Path, text: str) -> list[str]:
    errors = []
    for match in LINK_RE.finditer(text):
        target = match.group(1)
        if target.startswith(("http://", "https://", "mailto:", "#")):
            continue
        file_part = target.split("#", 1)[0]
        resolved = (path.parent / file_part).resolve()
        if not resolved.exists():
            errors.append(f"broken link '{target}'")
    return errors


def check_mermaid(path: Path, text: str) -> list[str]:
    fences = re.findall(r"^```(\w*)\s*$", text, flags=re.MULTILINE)
    if len(fences) % 2 != 0:
        return ["unbalanced code fences"]
    errors = []
    blocks = re.findall(r"^```mermaid\s*$(.*?)^```\s*$", text, flags=re.MULTILINE | re.DOTALL)
    for index, block in enumerate(blocks, start=1):
        first = next((line.strip() for line in block.splitlines() if line.strip()), "")
        if not re.match(r"^(C4Context|C4Container|C4Component|sequenceDiagram|flowchart|graph|erDiagram|classDiagram|stateDiagram)", first):
            errors.append(f"mermaid block {index} has unknown diagram type '{first[:30]}'")
    return errors


def main(argv: list[str]) -> int:
    only = None
    if len(argv) == 3 and argv[1] == "--only":
        only = argv[2]
    total_errors = 0
    for rel, headings in sorted(REQUIRED.items()):
        if only and not rel.startswith(only):
            continue
        path = ROOT / rel
        if not path.exists():
            print(f"ERROR {rel}: file missing")
            total_errors += 1
            continue
        text = path.read_text(encoding="utf-8")
        for message in check_headings(path, text, headings) + check_links(path, text) + check_mermaid(path, text):
            print(f"ERROR {rel}: {message}")
            total_errors += 1
    checked = sum(1 for rel in REQUIRED if not only or rel.startswith(only))
    print(f"checked {checked} documents, {total_errors} errors")
    return 1 if total_errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

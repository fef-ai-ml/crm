#!/usr/bin/env python3
"""Validate the docs/ tree: required files, required headings, relative links, Mermaid fences.

Usage: python3 tools/docs_check.py [--only PATH_PREFIX]
Exit 0 when clean, 1 when any ERROR line was printed (or no document matched a
given --only prefix), 2 when the command line itself is malformed.
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
        "## 6. Rule Compiler Mapping",
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

# Directories skipped entirely by the tree-wide link/Mermaid walk. Plans contain
# four-backtick wrapped documents whose inner fences are not meant to be parsed here.
EXCLUDED_DIRS = ("docs/superpowers/plans",)

LINK_RE = re.compile(r"\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
FENCE_RE = re.compile(r"^(`{3,})")

MERMAID_TYPES = (
    "C4Context",
    "C4Container",
    "C4Component",
    "C4Dynamic",
    "C4Deployment",
    "sequenceDiagram",
    "flowchart",
    "graph",
    "erDiagram",
    "classDiagram",
    "stateDiagram",
)

REQUIRED_FR_IDS = [f"FR-{n:02d}" for n in range(1, 26)]
REQUIRED_NFR_IDS = [f"NFR-{n:02d}" for n in range(1, 11)]


def non_fenced_lines(text: str) -> list[str]:
    """Lines of text that are not inside a fenced code block.

    A fence is any line matching ^(`{3,}); fences toggle keyed by their exact
    backtick count, so a four-backtick fence is not closed by a nested triple-
    backtick line (or vice versa).
    """
    stack: list[int] = []
    out: list[str] = []
    for line in text.splitlines():
        match = FENCE_RE.match(line)
        if match:
            count = len(match.group(1))
            if stack and stack[-1] == count:
                stack.pop()
            else:
                stack.append(count)
            continue
        if not stack:
            out.append(line)
    return out


def heading_present(lines: list[str], heading: str) -> bool:
    if heading == "# ":
        return any(line.startswith("# ") for line in lines)
    return any(line.rstrip() == heading for line in lines)


def check_headings(text: str, headings: list[str]) -> list[str]:
    lines = non_fenced_lines(text)
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


def check_mermaid(text: str) -> list[str]:
    stack: list[int] = []
    collecting: list[list[str] | None] = []
    blocks: list[list[str]] = []
    for line in text.splitlines():
        match = FENCE_RE.match(line)
        if match:
            count = len(match.group(1))
            if stack and stack[-1] == count:
                stack.pop()
                buf = collecting.pop()
                if buf is not None:
                    blocks.append(buf)
            else:
                info = line[len(match.group(1)):].strip()
                is_mermaid = not stack and info.split(" ", 1)[0] == "mermaid"
                stack.append(count)
                collecting.append([] if is_mermaid else None)
            continue
        if collecting and collecting[-1] is not None:
            collecting[-1].append(line)
    errors = []
    if stack:
        errors.append("unbalanced code fences")
    type_pattern = re.compile(r"^(" + "|".join(MERMAID_TYPES) + r")")
    for index, block in enumerate(blocks, start=1):
        first = next((line.strip() for line in block if line.strip()), "")
        if not type_pattern.match(first):
            errors.append(f"mermaid block {index} has unknown diagram type '{first[:30]}'")
    return errors


def check_brd_content(text: str) -> list[str]:
    return [f"missing requirement id '{token}'" for token in REQUIRED_FR_IDS + REQUIRED_NFR_IDS if token not in text]


def check_adr_index_content(text: str) -> list[str]:
    targets = {match.group(1).split("#", 1)[0] for match in LINK_RE.finditer(text)}
    errors = []
    for path in sorted((ROOT / "docs" / "adr").glob("[0-9][0-9][0-9][0-9]-*.md")):
        if path.name not in targets:
            errors.append(f"ADR index missing link to '{path.name}'")
    return errors


def parse_argv(argv: list[str]) -> str | None:
    """Return the --only prefix (or None), exiting with usage on any other shape."""
    args = argv[1:]
    if not args:
        return None
    if len(args) == 2 and args[0] == "--only":
        return args[1]
    print("usage: docs_check.py [--only PATH_PREFIX]", file=sys.stderr)
    raise SystemExit(2)


def is_excluded(rel: str) -> bool:
    return any(rel == d or rel.startswith(d + "/") for d in EXCLUDED_DIRS)


def tree_files(only: str | None) -> list[Path]:
    files = {p for p in ROOT.glob("docs/**/*.md") if not is_excluded(p.relative_to(ROOT).as_posix())}
    readme = ROOT / "README.md"
    if readme.exists():
        files.add(readme)
    selected = [p for p in files if not only or p.relative_to(ROOT).as_posix().startswith(only)]
    return sorted(selected)


def main(argv: list[str]) -> int:
    only = parse_argv(argv)

    total_errors = 0
    checked = 0
    manifest_paths: set[Path] = set()

    for rel, headings in sorted(REQUIRED.items()):
        if only and not rel.startswith(only):
            continue
        checked += 1
        path = ROOT / rel
        manifest_paths.add(path)
        if not path.exists():
            print(f"ERROR {rel}: file missing")
            total_errors += 1
            continue
        text = path.read_text(encoding="utf-8")
        errors = check_headings(text, headings) + check_links(path, text) + check_mermaid(text)
        if rel == "docs/brd/BRD.md":
            errors += check_brd_content(text)
        if rel == "docs/adr/README.md":
            errors += check_adr_index_content(text)
        for message in errors:
            print(f"ERROR {rel}: {message}")
            total_errors += 1

    extra_checked = 0
    for path in tree_files(only):
        if path in manifest_paths:
            continue
        extra_checked += 1
        rel = path.relative_to(ROOT).as_posix()
        text = path.read_text(encoding="utf-8")
        for message in check_links(path, text) + check_mermaid(text):
            print(f"ERROR {rel}: {message}")
            total_errors += 1

    if checked == 0 and extra_checked == 0:
        print("ERROR: no documents matched")
        return 1

    print(f"checked {checked} documents, {total_errors} errors")
    return 1 if total_errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
